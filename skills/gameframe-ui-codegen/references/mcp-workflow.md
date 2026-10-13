# Unity MCP 自动化示例

以下是传给 Unity `execute_code` 的 C# 方法体，不是放进 `Assets` 的新 Editor 工具。每段独立执行；示例采用 C# 6 语法，兼容当前工具的 CodeDom 后端。替换配置为本次任务的真实值，先核对工具 schema 与项目入口签名。

## 首次生成

目标须为已保存、尚未挂载生成宿主的 Prefab。执行完成后等待编译，下一次调用重新加载资产。

```csharp
var prefabPath = "Assets/Prefab/HitfixPrefab/ExamplePanel.prefab";
var scriptFolder = "Assets/Scripts/Hotfix/Example";
var className = "ExamplePanel";
var namespaceName = "Game.Hotfix";
var isItem = false;
var root = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
if (root == null)
{
    throw new System.InvalidOperationException("Prefab 不存在: " + prefabPath);
}

var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
if (stage != null && stage.assetPath == prefabPath)
{
    root = stage.prefabContentsRoot;
}

var actions = System.AppDomain.CurrentDomain.GetAssemblies()
    .Select(a => a.GetType("GameFrame.UI.Editor.UIBindActions"))
    .First(t => t != null);
var generate = actions.GetMethod("TryGenerate",
    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
var folderKey = "UIFrame.Gen.Folder";
var namespaceKey = "UIFrame.Gen.Namespace";
var hadFolder = EditorPrefs.HasKey(folderKey);
var hadNamespace = EditorPrefs.HasKey(namespaceKey);
var previousFolder = EditorPrefs.GetString(folderKey);
var previousNamespace = EditorPrefs.GetString(namespaceKey);
try
{
    object[] args = { root, className, scriptFolder, namespaceName, isItem, null };
    if (!(bool)generate.Invoke(null, args))
    {
        throw new System.InvalidOperationException((string)args[5]);
    }

    return "已排队生成；等待编译完成后检查宿主挂载。";
}
finally
{
    if (hadFolder)
    {
        EditorPrefs.SetString(folderKey, previousFolder);
    }
    else
    {
        EditorPrefs.DeleteKey(folderKey);
    }

    if (hadNamespace)
    {
        EditorPrefs.SetString(namespaceKey, previousNamespace);
    }
    else
    {
        EditorPrefs.DeleteKey(namespaceKey);
    }
}
```

## 添加组件绑定并写入

在宿主已挂载、无编译错误时执行。列表只列**尚未添加的绑定**。示例节点名要求各级唯一；出现同名节点时先取得准确 Component 对象，不照搬 `Transform.Find`。绑定 GameObject 时将入口改为 `TryAddGameObject`，仍传入该节点上的组件作为定位对象。

```csharp
var prefabPath = "Assets/Prefab/HitfixPrefab/ExamplePanel.prefab";
var hostTypeName = "Game.Hotfix.ExamplePanel";
var root = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
if (root == null)
{
    throw new System.InvalidOperationException("Prefab 不存在。");
}

var stage = UnityEditor.SceneManagement.PrefabStageUtility.GetCurrentPrefabStage();
if (stage != null && stage.assetPath == prefabPath)
{
    root = stage.prefabContentsRoot;
}

// 嵌套 Item 改为对应节点；节点路径相对于选定宿主。
var hostNode = root.transform;
var host = hostNode.GetComponents<MonoBehaviour>()
    .Single(c => c != null && c.GetType().FullName == hostTypeName);
Component[] components =
{
    hostNode.Find("Card/AccountInput").GetComponent<TMPro.TMP_InputField>(),
    hostNode.Find("Card/ConfirmLoginButton").GetComponent<UnityEngine.UI.Button>()
};
if (components.Any(c => c == null))
{
    throw new System.InvalidOperationException("目标组件不存在。");
}

var actions = System.AppDomain.CurrentDomain.GetAssemblies()
    .Select(a => a.GetType("GameFrame.UI.Editor.UIBindActions"))
    .First(t => t != null);
var add = actions.GetMethod("TryAddComponent", new[]
{
    typeof(Component), typeof(MonoBehaviour), typeof(string).MakeByRefType()
});
foreach (var component in components)
{
    object[] args = { component, host, null };
    if (!(bool)add.Invoke(null, args))
    {
        throw new System.InvalidOperationException((string)args[2]);
    }
}

var write = actions.GetMethod("TryWriteGen", new[]
{
    typeof(MonoBehaviour), typeof(string).MakeByRefType()
});
object[] writeArgs = { host, null };
if (!(bool)write.Invoke(null, writeArgs))
{
    throw new System.InvalidOperationException((string)writeArgs[1]);
}

return "已写入生成文件；等待编译后检查引用回填。";
```

## 已有绑定的刷新与删除

沿用上段获取的 `actions`、`host`，但每次工具调用都重新取得对象。

- 刷新：精确取得 `TryRefreshBinds(MonoBehaviour, out string)` 重载，使用 `object[] { host, null }` 调用并检查返回值与错误。先检查用户是否存在未写入删除，避免刷新将其补回。
- 删除：`actions.GetMethod("GetState").Invoke(null, new object[] { host })` 取得状态，通过该状态类型的 `Binds` 公共字段读取 `System.Collections.IList`，按每项 `FieldName` 唯一定位目标，再调用 `RemoveBind(state, index)`。不要假定索引跨刷新不变。最后调用 `TryWriteGen` 并等待编译。
- 多项删除：逐次按字段名重新定位，或在确认清单后按索引降序删除。字段不存在时核对状态，不静默忽略。

## 空闲但仍有待办时

确认编译、资源导入和 Domain Reload 均结束，再检查 `UIBindStore` 待办及 Console。`ProcessJobs` 处理整个 Store，可能影响其他 Prefab；只在确认待办归属后执行一次。

```csharp
if (EditorApplication.isCompiling || EditorApplication.isUpdating)
{
    throw new System.InvalidOperationException("Unity 尚未空闲。");
}

var hook = System.AppDomain.CurrentDomain.GetAssemblies()
    .Select(a => a.GetType("GameFrame.UI.Editor.UICompileHook"))
    .First(t => t != null);
hook.GetMethod("ProcessJobs",
    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)
    .Invoke(null, null);
return "待办已处理；重新读取 Prefab 核对引用。";
```

反射入口抛 `TargetInvocationException` 时查看 `InnerException` 和 Console 中的真实错误。等待中的首次任务与已失败清除标记的任务不同：后者修正原因后重新使用生成或写入入口排队。

## 从磁盘核验具体引用

完成 Stage 保存后执行。清单字段名取自本次实际 `.Gen.cs`；这里以 LoginPanel 为例。此段只读，不改动绑定或执行待办。

```csharp
var prefabPath = "Assets/Prefab/HitfixPrefab/LoginPanel.prefab";
var hostTypeName = "Game.Hotfix.LoginPanel";
var root = AssetDatabase.LoadAssetAtPath<GameObject>(prefabPath);
if (root == null)
{
    throw new System.InvalidOperationException("Prefab 不存在。");
}

var hostNode = root.transform;
var host = hostNode.GetComponents<MonoBehaviour>()
    .Single(c => c != null && c.GetType().FullName == hostTypeName);
var expected = new System.Collections.Generic.Dictionary<string, UnityEngine.Object>
{
    { "accountInput", hostNode.Find("Card/AccountInput").GetComponent<TMPro.TMP_InputField>() },
    { "passwordInput", hostNode.Find("Card/PasswordInput").GetComponent<TMPro.TMP_InputField>() },
    { "confirmLoginButton", hostNode.Find("Card/ConfirmLoginButton").GetComponent<UnityEngine.UI.Button>() },
    { "statusText", hostNode.Find("Card/StatusText").GetComponent<TMPro.TextMeshProUGUI>() }
};
var serialized = new SerializedObject(host);
foreach (var pair in expected)
{
    var property = serialized.FindProperty(pair.Key);
    if (pair.Value == null || property == null
        || property.propertyType != SerializedPropertyType.ObjectReference
        || property.objectReferenceValue != pair.Value)
    {
        throw new System.InvalidOperationException("回填不匹配: " + pair.Key);
    }
}

return new { prefab = prefabPath, verifiedFields = expected.Keys.ToArray() };
```
