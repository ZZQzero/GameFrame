# Luban binary runtime

Source: https://github.com/focus-creative-games/luban_unity/tree/061314200e2f03307c4d90b448e1de7b85431905

Pinned to Unity package `com.code-philosophy.luban` 1.2.0 at the same commit.
`ByteBuf.cs`, `BeanBase.cs`, `StringUtil.cs` and `LICENSE` are copied without changes.
Only the binary runtime is included; SimpleJSON and Unity Editor sources are not required.
Generation uses the existing Luban 5.0.0 tool in `Unity/Config/Luban`.
See `Docs/DotNetUsage.md` for generation, loading and deployment.
Builds do not access Unity's PackageCache. Update this source set explicitly together
with the generator/Unity runtime and validate both targets before changing versions.
