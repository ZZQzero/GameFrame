# GameFrame 代码编写规则

本规则适用于本仓库维护的 C# 代码，包括 AOT、Runtime、Hotfix、Editor 和 Tests。
第三方源码（包括 LoopScroll）、Packages 和自动生成代码不参与格式统一；生成代码需要调整时修改生成模板。

## 格式

- 使用 4 个空格缩进，不使用 Tab。
- 使用 Allman 大括号风格：左大括号单独换行，右大括号也单独占一行。
- `if`、`else if`、`else` 必须使用大括号，即使只有一条语句，或只是 `return`、`throw`、`continue`、`break`。
- 禁止将条件和执行语句写在同一行，禁止省略条件分支的大括号。
- `for`、`foreach`、`while`、`do` 和带语句体的 `using`、`lock` 同样使用换行大括号；允许使用 `using var` 声明。
- `try`、`catch`、`finally` 的语句体必须展开，不能写成单行代码块。
- 方法、类型和命名空间的代码块遵循相同规则。简单属性和单一表达式方法允许使用 `=>`。
- 控制流关键字与括号之间留一个空格，例如 `if (condition)`；方法调用名和括号之间不留空格。
- 每行一条语句，相关逻辑之间使用空行，避免将多段逻辑压缩到一行。

正确示例：

```csharp
if (config == null)
{
    throw new ArgumentNullException(nameof(config));
}
else if (!config.Enabled)
{
    return;
}
else
{
    Initialize(config);
}
```

禁止的写法：

```csharp
if (config == null) throw new ArgumentNullException(nameof(config));

if (!config.Enabled)
    return;

if (config.Enabled) { Initialize(config); }
```

## 命名与实现

- 类型、方法、属性和事件使用 PascalCase；参数、局部变量使用 camelCase。私有字段沿用所在模块的命名方式，避免无关重命名。
- 异步方法使用 `Async` 后缀，资源句柄在生命周期结束时释放，取消信号向下传递。
- 保持实现简单明确；启动失败停止后续初始化，不添加自动重试、兼容回退或失败回滚，除非任务明确要求。
- 只在能够处理错误或必须执行清理时使用异常处理；不吞掉异常，不堆砌重复校验和转发层。
- 注释解释约束、意图和必要的行为，不重复描述显而易见的代码。

## 修改与检查

- 新增代码和修改涉及的代码块遵循上述规则；全仓库格式化作为单独任务处理。
- 格式调整保持行为不变，不混入无关逻辑修改，保留用户已有改动。
- 提交前检查差异、空白和大括号；逻辑变更按影响范围进行编译或测试。
