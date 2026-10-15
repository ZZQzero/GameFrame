using System;
using System.Collections.Generic;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace GameFrame.Diagnostics
{
    [DisallowMultipleComponent]
    public sealed class RuntimeDebugConsole : MonoBehaviour
    {
        [SerializeField] Button closeButton;
        [SerializeField] Button clearButton;
        [SerializeField] Button copyAllButton;
        [SerializeField] TMP_Text status;
        [SerializeField] TMP_Text details;
        [SerializeField] ScrollRect detailsScroll;
        [SerializeField] TMP_InputField search;
        [SerializeField] Toggle logs;
        [SerializeField] Toggle warnings;
        [SerializeField] Toggle errors;
        [SerializeField] Toggle follow;
        [SerializeField] RuntimeLogList list;
        RuntimeLogBuffer buffer;
        RuntimeLogEntry selection;
        long displayedVersion = -1;
        float nextRefresh;
        float copiedUntil;
        bool resetPosition;

        public event Action CloseRequested;

        void Awake()
        {
            closeButton.onClick.AddListener(() => CloseRequested?.Invoke());
            clearButton.onClick.AddListener(Clear);
            copyAllButton.onClick.AddListener(CopyAll);
            search.onValueChanged.AddListener(FilterChanged);
            logs.onValueChanged.AddListener(FilterChanged);
            warnings.onValueChanged.AddListener(FilterChanged);
            errors.onValueChanged.AddListener(FilterChanged);
            follow.onValueChanged.AddListener(FollowChanged);
            list.Selected += Select;
            list.Browsing += StopFollowing;
        }

        public void Bind(RuntimeLogBuffer value)
        {
            buffer = value ?? throw new ArgumentNullException(nameof(value));
            displayedVersion = -1;
            selection = null;
            copiedUntil = 0;
            details.text = "Select a log above to view details and copy it.";
            resetPosition = true;
            Canvas.ForceUpdateCanvases();
            RefreshView();
        }

        public void Unbind()
        {
            buffer = null;
            selection = null;
        }

        void Update()
        {
            if (buffer == null || Time.unscaledTime < nextRefresh)
            {
                return;
            }
            nextRefresh = Time.unscaledTime + 0.1f;
            if (buffer.Version != displayedVersion)
            {
                RefreshView();
            }
            else if (copiedUntil > 0 && Time.unscaledTime >= copiedUntil)
            {
                copiedUntil = 0;
                displayedVersion = -1;
            }
        }

        void RefreshView()
        {
            var snapshot = buffer.Snapshot(out displayedVersion, out long dropped);
            var visible = new List<RuntimeLogEntry>();
            foreach (var entry in snapshot)
            {
                bool enabledType = entry.Type == LogType.Log ? logs.isOn :
                    entry.Type == LogType.Warning ? warnings.isOn : errors.isOn;
                if (enabledType && (search.text.Length == 0 ||
                    entry.Message.IndexOf(search.text, StringComparison.OrdinalIgnoreCase) >= 0 ||
                    entry.StackTrace.IndexOf(search.text, StringComparison.OrdinalIgnoreCase) >= 0))
                {
                    visible.Add(entry);
                }
            }
            list.SetEntries(visible, selection?.Id ?? -1, follow.isOn, resetPosition);
            resetPosition = false;
            if (Time.unscaledTime >= copiedUntil)
            {
                status.text = $"{visible.Count}/{snapshot.Length} logs" +
                    (dropped > 0 ? $" | {dropped} older logs discarded" : string.Empty);
            }
        }

        public void Select(RuntimeLogEntry entry)
        {
            selection = entry ?? throw new ArgumentNullException(nameof(entry));
            string text = entry.ToString();
            details.text = text;
            detailsScroll.StopMovement();
            detailsScroll.verticalNormalizedPosition = 1;
            list.SetSelection(entry.Id);
            GUIUtility.systemCopyBuffer = text;
            Copied("Copied selected log");
        }

        public void CopyAll()
        {
            GUIUtility.systemCopyBuffer = buffer.FormatAll();
            Copied("Copied all buffered logs");
        }

        public void Clear()
        {
            buffer.Clear();
            selection = null;
            details.text = "Select a log above to view details and copy it.";
            copiedUntil = 0;
            resetPosition = true;
            RefreshView();
        }

        void Copied(string message)
        {
            status.text = message;
            copiedUntil = Time.unscaledTime + 2;
        }

        void FilterChanged(string value) => InvalidateFilter();
        void FilterChanged(bool value) => InvalidateFilter();

        void InvalidateFilter()
        {
            resetPosition = true;
            displayedVersion = -1;
        }

        void FollowChanged(bool value)
        {
            displayedVersion = -1;
        }

        void StopFollowing()
        {
            follow.isOn = false;
        }
    }
}
