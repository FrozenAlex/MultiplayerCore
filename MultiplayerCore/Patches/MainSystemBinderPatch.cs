using BGNet.Core.GameLift;
using HarmonyLib;
using MultiplayerCore.Objects;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using Zenject;

namespace MultiplayerCore.Patches
{
    [HarmonyPatch(typeof(MainSystemInit), nameof(MainSystemInit.InstallBindings))]
    internal class MainSystemBinderPatch
    {
        private static readonly MethodInfo FromPrefab = AccessTools.Method(typeof(FromBinder),
            nameof(FromBinder.FromComponentInNewPrefab), new[] { typeof(UnityEngine.Object) });
        private static readonly FieldInfo EntitlementPrefab = AccessTools.Field(typeof(MainSystemInit), "_networkPlayerEntitlementCheckerPrefab");
        private static readonly MethodInfo ToSession = typeof(ConcreteBinderNonGeneric).GetMethods()
            .Single(m => m.Name == "To" && m.IsGenericMethodDefinition && m.GetGenericArguments().Length == 1 && m.GetParameters().Length == 0)
            .MakeGenericMethod(typeof(GameLiftPlayerSessionProvider));

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = instructions.ToList();
            int entitlementMatches = 0, sessionMatches = 0;
            for (int i = 0; i < codes.Count; i++)
            {
                MethodInfo? replacement = null;
                if (codes[i].Calls(ToSession))
                {
                    replacement = AccessTools.Method(typeof(MainSystemBinderPatch), nameof(BindSessionProvider));
                    sessionMatches++;
                }
                else if (i > 0 && codes[i].Calls(FromPrefab) && codes[i - 1].LoadsField(EntitlementPrefab))
                {
                    replacement = AccessTools.Method(typeof(MainSystemBinderPatch), nameof(BindEntitlementChecker));
                    entitlementMatches++;
                }
                if (replacement != null)
                    codes[i] = new CodeInstruction(codes[i]) { opcode = OpCodes.Call, operand = replacement };
            }
            if (entitlementMatches != 1 || sessionMatches != 1)
                throw new InvalidOperationException($"Unsupported multiplayer bindings: entitlement={entitlementMatches}, session={sessionMatches}");
            return codes;
        }

        private static ScopeConcreteIdArgConditionCopyNonLazyBinder BindEntitlementChecker(
            ConcreteIdBinderGeneric<NetworkPlayerEntitlementChecker> contract, UnityEngine.Object prefab)
            => contract.To<MpEntitlementChecker>().FromNewComponentOnRoot();

        private static FromBinderNonGeneric BindSessionProvider(ConcreteBinderNonGeneric contract)
            => contract.To<CustomPlayerSessionProvider>();
    }
}
