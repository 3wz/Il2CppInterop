using System;
using System.Linq;
using System.Runtime.InteropServices;
using Il2CppInterop.Common;
using Il2CppInterop.Common.XrefScans;
using Il2CppInterop.Runtime.Runtime;
using Il2CppInterop.Runtime.Startup;
using Microsoft.Extensions.Logging;

namespace Il2CppInterop.Runtime.Injection.Hooks
{
    internal unsafe class MetadataCache_GetTypeInfoFromTypeDefinitionIndex_Hook :
        Hook<MetadataCache_GetTypeInfoFromTypeDefinitionIndex_Hook.MethodDelegate>
    {
        public override string TargetMethodName => "MetadataCache::GetTypeInfoFromTypeDefinitionIndex";
        public override MethodDelegate GetDetour() => Hook;

        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        internal delegate Il2CppClass* MethodDelegate(int index);

        private Il2CppClass* Hook(int index)
        {
            if (InjectorHelpers.s_InjectedClasses.TryGetValue(index, out IntPtr classPtr))
                return (Il2CppClass*)classPtr;

            return Original(index);
        }

        private IntPtr FindGetTypeInfoFromTypeDefinitionIndex(bool forceICallMethod = false)
        {
            IntPtr getTypeInfoFromTypeDefinitionIndex = IntPtr.Zero;

            // il2cpp_image_get_class is added in 2018.3.0f1
            if (Il2CppInteropRuntime.Instance.UnityVersion < new Version(2018, 3, 0) || forceICallMethod)
            {
                // (Kasuromi): RuntimeHelpers.InitializeArray calls an il2cpp icall, proxy function does some magic before it invokes it
                // https://github.com/Unity-Technologies/mono/blob/unity-2018.2/mcs/class/corlib/System.Runtime.CompilerServices/RuntimeHelpers.cs#L53-L54
                IntPtr runtimeHelpersInitializeArray = InjectorHelpers.GetIl2CppMethodPointer(
                    typeof(Il2CppSystem.Runtime.CompilerServices.RuntimeHelpers)
                        .GetMethod("InitializeArray", new Type[] { typeof(Il2CppSystem.Array), typeof(IntPtr) })
                );
                Logger.Instance.LogTrace("Il2CppSystem.Runtime.CompilerServices.RuntimeHelpers::InitializeArray: 0x{RuntimeHelpersInitializeArrayAddress}", runtimeHelpersInitializeArray.ToInt64().ToString("X2"));

                var runtimeHelpersInitializeArrayICall = XrefScannerLowLevel.JumpTargets(runtimeHelpersInitializeArray).Last();
                if (XrefScannerLowLevel.JumpTargets(runtimeHelpersInitializeArrayICall).Count() == 1)
                {
                    // is a thunk function
                    Logger.Instance.LogTrace("RuntimeHelpers::thunk_InitializeArray: 0x{RuntimeHelpersInitializeArrayICallAddress}", runtimeHelpersInitializeArrayICall.ToInt64().ToString("X2"));
                    runtimeHelpersInitializeArrayICall = XrefScannerLowLevel.JumpTargets(runtimeHelpersInitializeArrayICall).Single();
                }

                Logger.Instance.LogTrace("RuntimeHelpers::InitializeArray: 0x{RuntimeHelpersInitializeArrayICallAddress}", runtimeHelpersInitializeArrayICall.ToInt64().ToString("X2"));

                var typeGetUnderlyingType = XrefScannerLowLevel.JumpTargets(runtimeHelpersInitializeArrayICall).ElementAt(1);
                Logger.Instance.LogTrace("Type::GetUnderlyingType: 0x{TypeGetUnderlyingTypeAddress}", typeGetUnderlyingType.ToInt64().ToString("X2"));

                getTypeInfoFromTypeDefinitionIndex = XrefScannerLowLevel.JumpTargets(typeGetUnderlyingType).First();
            }
            else
            {
                var imageGetClassAPI = InjectorHelpers.GetIl2CppExport(nameof(IL2CPP.il2cpp_image_get_class));
                Logger.Instance.LogTrace("il2cpp_image_get_class: 0x{ImageGetClassApiAddress}", imageGetClassAPI.ToInt64().ToString("X2"));

                var imageGetType = XrefScannerLowLevel.JumpTargets(imageGetClassAPI).First();
                Logger.Instance.LogTrace("Image::GetType: 0x{ImageGetTypeAddress}", imageGetType.ToInt64().ToString("X2"));

                var imageGetTypeXrefs = XrefScannerLowLevel.JumpTargets(imageGetType).ToArray();

                if (imageGetTypeXrefs.Length == 0)
                {
                    // (Kasuromi): Image::GetType appears to be inlined in il2cpp_image_get_class on some occasions,
                    // if the unconditional xrefs are 0 then we are in the correct method (seen on unity 2019.3.15)
                    getTypeInfoFromTypeDefinitionIndex = imageGetType;
                }
                else getTypeInfoFromTypeDefinitionIndex = imageGetTypeXrefs[0];
                if ((getTypeInfoFromTypeDefinitionIndex.ToInt64() & 0xF) != 0)
                {
                    Logger.Instance.LogTrace("Image::GetType xref wasn't aligned, attempting to resolve from icall");
                    return FindGetTypeInfoFromTypeDefinitionIndex(true);
                }
                if (imageGetTypeXrefs.Count() > 1 && UnityVersionHandler.IsMetadataV29OrHigher)
                {
                    // (Kasuromi): metadata v29 introduces handles and adds extra calls, a check for unity versions might be necessary in the future

                    Logger.Instance.LogTrace($"imageGetTypeXrefs.Length: {imageGetTypeXrefs.Length}");

                    // If the game is built as IL2CPP Master, GetAssemblyTypeHandle is inlined, xrefs length is 3 and it's the first function call,
                    // if not, it's the last call.
                    var getTypeInfoFromHandle = imageGetTypeXrefs.Length == 2 ? imageGetTypeXrefs.Last() : imageGetTypeXrefs.First();

                    Logger.Instance.LogTrace($"getTypeInfoFromHandle: {getTypeInfoFromHandle:X2}");

                    var getTypeInfoFromHandleXrefs = XrefScannerLowLevel.JumpTargets(getTypeInfoFromHandle).ToArray();

                    // If getTypeInfoFromHandle xrefs is not a single call, it's the function we want, if not, we keep xrefing until we find it
                    if (getTypeInfoFromHandleXrefs.Length != 1)
                    {
                        getTypeInfoFromTypeDefinitionIndex = getTypeInfoFromHandle;
                        Logger.Instance.LogTrace($"Xrefs length was not 1, getTypeInfoFromTypeDefinitionIndex: {getTypeInfoFromTypeDefinitionIndex:X2}");
                    }
                    else
                    {
                        // Two calls, second one (GetIndexForTypeDefinitionInternal) is inlined
                        getTypeInfoFromTypeDefinitionIndex = getTypeInfoFromHandleXrefs.Single();
                        // Xref scanner is sometimes confused about getTypeInfoFromHandle so we walk all the thunks until we hit the big method we need
                        while (XrefScannerLowLevel.JumpTargets(getTypeInfoFromTypeDefinitionIndex).ToArray().Length == 1)
                        {
                            getTypeInfoFromTypeDefinitionIndex = XrefScannerLowLevel.JumpTargets(getTypeInfoFromTypeDefinitionIndex).Single();
                        }
                    }
                }
            }

            return getTypeInfoFromTypeDefinitionIndex;
        }

        /// <summary>
        /// On stock il2cpp the xref walk from Image::GetType lands on MetadataCache::GetTypeInfoFromTypeDefinitionIndex,
        /// which takes a 32-bit type definition index. HybridCLR (and similar il2cpp restructurings) inserts a
        /// MetadataCache::GetTypeInfoFromHandle hop in between; that function takes an Il2CppMetadataTypeHandle pointer,
        /// so hooking it hands a small integer to code that dereferences its first argument and the process dies with an
        /// access violation on the first call. Reject candidates that plainly dereference rcx instead of using it as an index.
        /// </summary>
        private static unsafe bool LooksLikeIndexSignature(IntPtr method)
        {
            var code = (byte*)method;

            if (DereferencesFirstArgument(code)) return false;

            // HybridCLR resolves Image::GetType through a tail-call wrapper instead of the metadata cache directly:
            //   sub rsp, 0x28 ; call rel32 ; mov ecx, eax ; add rsp, 0x28 ; jmp rel32
            // The wrapper forwards rcx unchanged, so the callee is the one that decides the argument type.
            if (code[0] == 0x48 && code[1] == 0x83 && code[2] == 0xEC && code[3] == 0x28 && code[4] == 0xE8)
            {
                var callee = (byte*)((nint)method + 9 + *(int*)(code + 5));
                if (DereferencesFirstArgument(callee)) return false;
            }

            return true;
        }

        private static unsafe bool DereferencesFirstArgument(byte* code)
        {
            // mov eax/ecx/edx, [rcx+disp8]  ->  8B 41/49/51 xx
            if (code[0] == 0x8B && (code[1] == 0x41 || code[1] == 0x49 || code[1] == 0x51)) return true;
            // mov eax/ecx/edx, [rcx]        ->  8B 01/09/11
            if (code[0] == 0x8B && (code[1] == 0x01 || code[1] == 0x09 || code[1] == 0x11)) return true;
            // mov rax, [rcx+disp8]          ->  48 8B 41/49/51 xx
            if (code[0] == 0x48 && code[1] == 0x8B && (code[2] == 0x41 || code[2] == 0x49 || code[2] == 0x51)) return true;
            // mov rax, [rcx]                ->  48 8B 01
            if (code[0] == 0x48 && code[1] == 0x8B && code[2] == 0x01) return true;
            return false;
        }

        public override IntPtr FindTargetMethod()
        {
            IntPtr target = FindGetTypeInfoFromTypeDefinitionIndex();
            if (target != IntPtr.Zero && !LooksLikeIndexSignature(target))
            {
                Logger.Instance.LogWarning(
                    "MetadataCache::GetTypeInfoFromTypeDefinitionIndex resolved to 0x{TargetAddress}, which does not take a type definition index; skipping hook. Injected types are still resolved via Class::FromIl2CppType and Class::FromName.",
                    target.ToInt64().ToString("X2"));
                return IntPtr.Zero;
            }
            return target;
        }

        /// <summary>
        /// This hook is an optimisation for injected type lookups, not a requirement: Class::FromIl2CppType and
        /// Class::FromName resolve the same injections. Keep the runtime alive when the target cannot be located.
        /// </summary>
        public override void TargetMethodNotFound()
        {
            Logger.Instance.LogTrace("MetadataCache::GetTypeInfoFromTypeDefinitionIndex hook skipped");
        }
    }
}
