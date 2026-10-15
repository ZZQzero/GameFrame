using System;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GameFrame.Diagnostics
{
    public sealed class RuntimeLogRow : MonoBehaviour
    {
        [SerializeField] TMP_Text label;
        [SerializeField] Image background;
        [SerializeField] Button button;
        Action<RuntimeLogEntry> selected;
        RuntimeLogEntry entry;

        void Awake()
        {
            button.onClick.AddListener(Select);
        }

        internal void Bind(RuntimeLogEntry value, long selectedId, Action<RuntimeLogEntry> onSelected)
        {
            entry = value;
            selected = onSelected;
            string summary = value.Message.Length > 180 ? value.Message.Substring(0, 180) : value.Message;
            label.text = $"{value.Time:HH:mm:ss.fff}  {value.Type,-9}  {summary.Replace('\n', ' ').Replace('\r', ' ')}";
            label.color = value.Type == LogType.Warning ? new Color(1f, 0.79f, 0.36f) :
                value.Type == LogType.Log ? new Color(0.86f, 0.9f, 0.96f) : new Color(1f, 0.43f, 0.43f);
            background.color = value.Id == selectedId ? new Color(0.17f, 0.32f, 0.49f) :
                value.Id % 2 == 0 ? new Color(0.1f, 0.13f, 0.18f) : new Color(0.08f, 0.1f, 0.14f);
        }

        void Select()
        {
            selected?.Invoke(entry);
        }
    }
}
