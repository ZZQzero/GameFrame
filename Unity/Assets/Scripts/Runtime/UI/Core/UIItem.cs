using UnityEngine;

namespace GameFrame.UI
{
    /// <summary>
    /// 列表 Item/Cell。不进 UI 栈、不走 <see cref="GameUI.Register{T}"/>。
    /// 运行时等价于 MonoBehaviour；基类只为编辑器把绑定画在脚本 Inspector 上。
    /// </summary>
    public abstract class UIItem : MonoBehaviour
    {
    }
}
