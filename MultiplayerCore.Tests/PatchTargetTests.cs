using Mono.Cecil;
using Xunit;

// Metadata-only validation: no Unity engine, mod initialization or live patching is invoked.
public class PatchTargetTests
{
    [Fact]
    public void HarmonyAndAffinityTargetsMatchTheSuppliedGame()
    {
        var game = Environment.GetEnvironmentVariable("BEATSABER_TEST_GAME")
            ?? throw new InvalidOperationException("Set BEATSABER_TEST_GAME to the 1.45.3 installation.");
        var core = Environment.GetEnvironmentVariable("MULTIPLAYERCORE_TEST_DLL")
            ?? throw new InvalidOperationException("Set MULTIPLAYERCORE_TEST_DLL to the built core DLL.");
        var bt = Environment.GetEnvironmentVariable("BEATTOGETHER_TEST_DLL")
            ?? throw new InvalidOperationException("Set BEATTOGETHER_TEST_DLL to the built selector DLL.");
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.Combine(game, "Beat Saber_Data", "Managed"));
        resolver.AddSearchDirectory(Path.Combine(game, "Libs"));
        resolver.AddSearchDirectory(Environment.GetEnvironmentVariable("MULTIPLAYER_TEST_MODREFS") ?? Path.Combine(game, "Plugins"));
        resolver.AddSearchDirectory(Path.GetDirectoryName(core)!);
        resolver.AddSearchDirectory(Path.GetDirectoryName(bt)!);
        var failures = new List<string>();
        int checkedMethods = 0;
        foreach (var mod in new[] { core, bt })
        {
            using var assembly = AssemblyDefinition.ReadAssembly(mod, new ReaderParameters { AssemblyResolver = resolver });
            foreach (var type in assembly.MainModule.Types)
            foreach (var patch in type.Methods)
            {
                var targets = patch.CustomAttributes.Where(IsTarget).ToList();
                if (targets.Count == 0 && patch.CustomAttributes.Any(a => a.AttributeType.Name is "HarmonyPrefix" or "HarmonyPostfix" or "HarmonyTranspiler") ||
                    targets.Count == 0 && patch.Name is "Transpiler" or "Prefix" or "Postfix")
                    targets = type.CustomAttributes.Where(IsTarget).ToList();
                foreach (var attr in targets)
                {
                    var targetType = attr.ConstructorArguments.Select(a => a.Value).OfType<TypeReference>().FirstOrDefault();
                    var targetName = attr.ConstructorArguments.Select(a => a.Value).OfType<string>().FirstOrDefault();
                    if (targetType == null || targetName == null) continue;
                    var definition = targetType.Resolve();
                    var methods = Hierarchy(definition).SelectMany(t => t.Methods)
                        .Where(m => m.Name == targetName || m.Name == "get_" + targetName || m.Name == "set_" + targetName).ToArray();
                    var label = type.Name + "." + patch.Name + " -> " + targetType.Name + "." + targetName;
                    checkedMethods++;
                    if (methods.Length == 0) { failures.Add(label + " missing method"); continue; }
                    foreach (var parameter in patch.Parameters)
                    {
                        if (parameter.Name.StartsWith("___"))
                        {
                            var field = parameter.Name.Substring(3);
                            if (!Hierarchy(definition).SelectMany(t => t.Fields).Any(f => f.Name == field))
                                failures.Add(label + " missing field " + field);
                        }
                        else if (!parameter.Name.StartsWith("__") && parameter.Name != "instructions" && parameter.Name != "gen" && parameter.Name != "generator")
                        {
                            if (!methods.Any(m => m.Parameters.Any(p => p.Name == parameter.Name)))
                                failures.Add(label + " missing argument " + parameter.Name);
                        }
                    }
                }
            }
        }
        Console.WriteLine($"Validated {checkedMethods} Harmony/Affinity patch targets against the installed game.");
        Assert.True(checkedMethods >= 40, $"Only checked {checkedMethods} patch methods.");
        Assert.True(failures.Count == 0, string.Join(Environment.NewLine, failures));
    }
    [Fact]
    public void TypedTranspilerReplacementsMatch1453BindingsAndPlayers()
    {
        var game = Environment.GetEnvironmentVariable("BEATSABER_TEST_GAME")!;
        var core = Environment.GetEnvironmentVariable("MULTIPLAYERCORE_TEST_DLL")!;
        using var gameInit = AssemblyDefinition.ReadAssembly(Path.Combine(game, "Beat Saber_Data/Managed/GameInit.dll"));
        using var main = AssemblyDefinition.ReadAssembly(Path.Combine(game, "Beat Saber_Data/Managed/Main.dll"));
        using var mp = AssemblyDefinition.ReadAssembly(Path.Combine(game, "Beat Saber_Data/Managed/BeatSaber.Multiplayer.Core.dll"));
        using var mod = AssemblyDefinition.ReadAssembly(core);
        MethodDefinition Method(AssemblyDefinition a, string type, string name) => a.MainModule.Types.Single(t => t.Name == type).Methods.Single(m => m.Name == name);
        var bindings = Method(gameInit, "MainSystemInit", "InstallBindings").Body.Instructions;
        var toSession = Assert.Single(bindings.Select(i => i.Operand).OfType<GenericInstanceMethod>(),
            m => m.Name == "To" && m.GenericArguments.Any(t => t.Name == "GameLiftPlayerSessionProvider"));
        var replacement = Method(mod, "MainSystemBinderPatch", "BindSessionProvider");
        Assert.Equal(toSession.DeclaringType.FullName, replacement.Parameters[0].ParameterType.FullName);
        Assert.Equal(toSession.ReturnType.FullName, replacement.ReturnType.FullName);
        var players = Method(main, "MultiplayerPlayersManager", "get_allActiveAtGameStartPlayers");
        foreach (var type in new[] { "IntroAnimationPatches", "OutroAnimationPatches" })
            Assert.Equal(players.ReturnType.FullName, Method(mod, type, "GetActivePlayersAttacher").ReturnType.FullName);
        Assert.Contains(Method(mp, "CompatibilityVersion", "Get").Body.Instructions, i => (i.Operand as string) == "A31AF687");
        // Replacing +1 with +0 preserves the evaluation stack and branch labels in CalculateLayout.
        var layout = Method(main, "MultiplayerLayoutProvider", "CalculateLayout").Body.Instructions;
        Assert.Single(layout.Where((i, n) => i.OpCode == Mono.Cecil.Cil.OpCodes.Ldc_I4_1 && n + 1 < layout.Count && layout[n + 1].OpCode == Mono.Cecil.Cil.OpCodes.Add));
    }

    [Fact]
    public void ServerSwitchResolvesThePrivateClientOnItsDeclaringType()
    {
        var game = Environment.GetEnvironmentVariable("BEATSABER_TEST_GAME")!;
        var resolver = new DefaultAssemblyResolver();
        resolver.AddSearchDirectory(Path.Combine(game, "Beat Saber_Data", "Managed"));
        resolver.AddSearchDirectory(Path.Combine(game, "Libs"));
        using var mod = AssemblyDefinition.ReadAssembly(Environment.GetEnvironmentVariable("MULTIPLAYERCORE_TEST_DLL")!,
            new ReaderParameters { AssemblyResolver = resolver });
        var provider = mod.MainModule.Types.Single(t => t.Name == "CustomPlayerSessionProvider");
        var update = provider.Methods.Single(m => m.Name == "UpdateServer");
        var accessor = Assert.Single(update.Body.Instructions.Select(i => i.Operand).OfType<GenericInstanceMethod>(), m => m.Name == "SetField");
        // A derived-type FieldAccessor cannot find a private field declared on the base.
        Assert.Equal(provider.BaseType.FullName, accessor.GenericArguments[0].FullName);
        var field = Assert.Single(provider.BaseType.Resolve().Fields, f => f.Name == "_graphAPIClient");
        Assert.Equal(field.FieldType.FullName, accessor.GenericArguments[1].FullName);
    }

    private static bool IsTarget(CustomAttribute a) => a.AttributeType.Name is "HarmonyPatch" or "AffinityPatchAttribute";
    private static IEnumerable<TypeDefinition> Hierarchy(TypeDefinition t)
    {
        yield return t;
        while (t.BaseType != null && t.BaseType.FullName != "System.Object")
        {
            t = t.BaseType.Resolve(); yield return t;
        }
    }
}
