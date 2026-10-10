using System;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Game.Hotfix
{
    [DisallowMultipleComponent]
    public sealed class WoodenFishHitTarget : MonoBehaviour, IPointerDownHandler
    {
        public event Action Pressed;

        public void OnPointerDown(PointerEventData eventData)
        {
            if (isActiveAndEnabled && eventData.button == PointerEventData.InputButton.Left)
            {
                Pressed?.Invoke();
            }
        }
    }
}
