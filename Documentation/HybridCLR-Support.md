# HybridCLR 支持

本文记录让 Il2CppInterop 在 **HybridCLR 热更** 的 Unity IL2CPP 游戏上稳定工作的三处修复，以及在 BepInEx / MelonLoader 里怎么用。

验证环境：Unity 2022.3.20f1 + HybridCLR（游戏 `Zqtx` 1.2.0，`GameAssembly.dll` 63.9 MB，386 导出）+ BepInEx 6.0.0 + Il2CppInterop 1.5.3。

## 症状

1. MelonLoader 0.6.6 / 0.7.3、BepInEx 6.0.0 启动即崩（`coreclr.dll` 里 `0xC0000005`），日志停在
   `Class::Init signatures have been exhausted, using a substitute!` 之后。
2. 不崩的情况下，注入的 `MonoBehaviour` 不跑 `Start` / `Update`（vtable 没初始化）。
3. `IL2CPP.GetIl2CppClass(assembly, ns, class)` 对热更程序集与 AOT 程序集都返回 `0`，日志报 `Assembly X is not registered in il2cpp`。

## 三处修复

### 1. `Class::Init` 用 `il2cpp_runtime_class_init`

`InjectorHelpers.s_ClassInitSignatures` 的两条字节签名

```
E8 ?? ?? ?? ?? 0F B7 47 28 83
E8 ?? ?? ?? ?? 0F B7 47 48 48
```

在 HybridCLR 重编译过的 `GameAssembly` 上一条都匹配不到，于是走进 `GetClassInitSubstitute()`。原 fallback 首选 `mono_class_instance_size` —— 那个函数**只返回实例大小，不做递归 vtable 初始化**，于是注入的类型拿不到虚表槽位，Unity 调度不到 `Start` / `Update`。

改成优先取 `il2cpp_runtime_class_init`：它是 `Class::Init` 的标准 C 入口，每份 il2cpp 都导出，且签名 `void(Il2CppClass*)` 与 `d_ClassInit` 完全一致。原 fallback 保留在后面（老版本 il2cpp 若没有这个导出仍能降级）。

### 2. `MetadataCache::GetTypeInfoFromTypeDefinitionIndex` 装错函数要跳过而不是硬装

崩溃点（实测）：hook 被装到 `GameAssembly+0x3676E0`，而那里是

```
sub rsp, 0x28
call 0x365D70      ; 真实现
mov  ecx, eax
add  rsp, 0x28
jmp  ...
```

`0x365D70` 的首指令是 `8B 41 08` = `mov eax, [rcx+8]` —— 它把第一个参数当**指针**解引用。而 `GetTypeInfoFromTypeDefinitionIndex` 的 delegate 是 `Il2CppClass*(int index)`，`base.Original(index)` 把一个小整数放进 rcx，于是读到 `0xFFFFFFFF7311D738` 这种符号扩展假指针 → `0xC0000005`。

HybridCLR 下 `Image::GetType` 的 xref 链比原版多一层 wrapper，解析结果落在 wrapper 上，而旧代码只做 `while (JumpTargets(x).Length == 1) x = JumpTargets(x).Single()`，跳过 `call` 形态的 tail-call。

修复：解析出目标后先判首参形态——`8B 41/49/51`、`8B 01/09/11`、`48 8B 41/49/51`、`48 8B 01` 任一命中即为「首参是指针」；若目标是 `48 83 EC 28 E8` 形态的 wrapper，则按 `(nint)method + 9 + *(int*)(code + 5)` 跟到 callee 再判一次。不合格就 `LogWarning` 并返回 `IntPtr.Zero` 跳过这个 hook，同时把基类 `TargetMethodNotFound()` 覆盖成不抛异常。

这个 hook 只是让「注入类型按 token 反查」更快，跳过不影响正确性——注入类型仍然通过 `Class::FromIl2CppType` 与 `Class::FromName` 解析。

### 3. image 名带 `.dll` 后缀 + 程序集表快照过期

`IL2CPP` 用 `il2cpp_image_get_name()` 的返回值当 key，而 HybridCLR 注册热更 image 时名字保留 `.dll`（`HotUpdateScripts.dll`），调用方（含 `Il2CppClassPointerStore`）传的是裸名 `HotUpdateScripts`。

另外 `ourImagesMap` 是静态构造函数里的**一次性快照**，而热更程序集要等游戏进到 boot 流程才被 HybridCLR 加载进 il2cpp domain —— 快照里永远没有它们。

修复：

- 注册时同时索引 `Foo.dll` 与 `Foo` 两种拼写（`TryAdd`，不覆盖合法裸名）。
- `GetIl2CppImages()` 用 `Distinct()`——`InjectorHelpers.AddTypeToLookup` 会为每个返回的指针 `Add` 一行 lookup，重复指针会抛 `ArgumentException: An item with the same key has already been added`。
- 查不到时重扫 domain（`RefreshImagesIfStale()`，500 ms 节流，避免未知名反复全量扫）。
- `ourImagesMap` 换成 `ConcurrentDictionary`，重扫与并发查找不再互相踩。

### 附加：去掉 TerraFX.Interop.Windows 依赖

`MemoryUtils` 为了在 Unity 6 上跳过 guard page，用了 TerraFX 的 `VirtualQuery` + `MEMORY_BASIC_INFORMATION`，这会引入一个 **17.7 MB** 的包，而 mod loader 的发行包里没有它——从源码编译替换 `Il2CppInterop.Runtime.dll` 时会直接 `FileNotFoundException: TerraFX.Interop.Windows`。

改成文件内自带的 kernel32 P/Invoke（`MEMORY_BASIC_INFORMATION` x64 布局，`sizeof == 48`），行为不变、产物零附加依赖。

## 非 HybridCLR 项目的行为

三处修复都是「先按原路径，失败才降级」：

- 签名扫描成功 → 用真 `Class::Init`，不会碰 `il2cpp_runtime_class_init`。
- 目标函数首参是整数 → 校验通过，hook 正常安装。
- image 名不带 `.dll` → 别名分支不触发。
- `VirtualQuery` 声明与 TerraFX 的调用语义一致（同一 kernel32 导出、同一结构体）。

## 构建与部署

```powershell
cd D:\Il2CppInterop
dotnet build Il2CppInterop.sln -c Release
```

产物：

- `bin\Il2CppInterop.Runtime\net6.0\Il2CppInterop.Runtime.dll`
- `bin\Il2CppInterop.HarmonySupport\net6.0\Il2CppInterop.HarmonySupport.dll`

把它们覆盖到 `BepInEx\core\` 即可，**不需要**再额外放 `TerraFX.Interop.Windows.dll`。

BepInEx 侧不用改代码：它通过 `PackageReference` 拿 Il2CppInterop，修复在 Il2CppInterop 层。只有在你想让「自己编译的 BepInEx 发行包」自带这版 Il2CppInterop 时，才需要把上面两个 dll 打进 core 目录（或在本地打一个 1.5.3 的 nupkg 让它优先）。

MelonLoader 的内置 Il2CppInterop 是另一条版本线（1.4.x），本补丁针对 1.5.3 源码，需要按同样三处逻辑自行 port。

## 验证证据（2026-11-26，本机）

```
[Warning:Il2CppInterop] MetadataCache::GetTypeInfoFromTypeDefinitionIndex resolved to 0x7FF8714376E0,
  which does not take a type definition index; skipping hook.
[Debug  :Il2CppInterop] MetadataCache::GetTypeInfoFromTypeDefinitionIndex hook skipped
[Warning:Il2CppInterop] Class::Init signatures have been exhausted, using a substitute!
[Debug  :Il2CppInterop] Picked il2cpp_runtime_class_init as a Class::Init substitute
[Debug  :Il2CppInterop] Class::Init: 0x7FF871467F60          # = GameAssembly base + 0x397F60
[Info   :   BepInEx] Loading [ZqtxProbe 0.4.0]
[Message:   BepInEx] Chainloader startup complete
[Info   : ZqtxProbe] [DRIVE] GetIl2CppClass(VideoListManager) = 0x11984D24ED0   # 热更类型
[Info   : ZqtxProbe] [DRIVE] GetIl2CppClass(GameManager)      = 0x11984A7A7B0   # 热更类型
[Info   : ZqtxProbe] [DRIVE] GetIl2CppClass(AOTBoot, AOT sanity) = 0x11982152EF0
```

`D:\zqtx-lab\probe-start.log` / `probe-update.log` 有 `Start called` 与 `Update 1/2/3`，证明注入组件被 Unity 正常调度；整份日志 120 KB、`[Error` 0 条。
