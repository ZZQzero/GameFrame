using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace GameFrame.Diagnostics
{
    /// <summary>为既有 LoopScroll 提供本地行复用，不依赖资源包或框架池。</summary>
    public sealed class RuntimeLogList : MonoBehaviour, LoopScrollPrefabSource, LoopScrollDataSource,
        IBeginDragHandler, IScrollHandler
    {
        [SerializeField] LoopVerticalScrollRect scroll;
        [SerializeField] RuntimeLogRow rowTemplate;
        [SerializeField] Transform poolRoot;
        readonly Stack<RuntimeLogRow> pool = new Stack<RuntimeLogRow>();
        IReadOnlyList<RuntimeLogEntry> entries = Array.Empty<RuntimeLogEntry>();
        long selectedId;

        public event Action<RuntimeLogEntry> Selected;
        public event Action Browsing;

        void Awake()
        {
            scroll.prefabSource = this;
            scroll.dataSource = this;
        }

        public void SetEntries(IReadOnlyList<RuntimeLogEntry> values, long selection, bool follow, bool resetPosition)
        {
            if (!follow && !resetPosition && entries.Count > 0 && values.Count >= entries.Count &&
                values[0].Id == entries[0].Id)
            {
                entries = values;
                selectedId = selection;
                scroll.totalCount = values.Count;
                scroll.RefreshCells();
                return;
            }

            int first = 0;
            float offset = 0;
            if (!resetPosition && entries.Count > 0 && scroll.totalCount > 0)
            {
                int oldFirst = Mathf.Clamp(scroll.GetFirstItem(out offset), 0, entries.Count - 1);
                long anchorId = entries[oldFirst].Id;
                for (int i = 0; i < values.Count; i++)
                {
                    if (values[i].Id >= anchorId)
                    {
                        first = i;
                        break;
                    }
                }
            }

            entries = values;
            selectedId = selection;
            scroll.totalCount = values.Count;
            if (values.Count == 0)
            {
                scroll.ClearCells();
            }
            else if (follow)
            {
                scroll.RefillCellsFromEnd();
            }
            else
            {
                scroll.RefillCells(first, resetPosition ? 0 : Mathf.Max(0, offset));
            }
        }

        public void SetSelection(long id)
        {
            selectedId = id;
            scroll.RefreshCells();
        }

        public GameObject GetObject(int index)
        {
            var row = pool.Count > 0 ? pool.Pop() : Instantiate(rowTemplate, poolRoot);
            return row.gameObject;
        }

        public void ReturnObject(Transform item)
        {
            item.gameObject.SetActive(false);
            item.SetParent(poolRoot, false);
            pool.Push(item.GetComponent<RuntimeLogRow>());
        }

        public void ProvideData(Transform item, int index)
        {
            item.GetComponent<RuntimeLogRow>().Bind(entries[index], selectedId, Select);
        }

        void Select(RuntimeLogEntry entry)
        {
            Selected?.Invoke(entry);
        }

        public void OnBeginDrag(PointerEventData eventData)
        {
            Browsing?.Invoke();
        }

        public void OnScroll(PointerEventData eventData)
        {
            Browsing?.Invoke();
        }
    }
}
