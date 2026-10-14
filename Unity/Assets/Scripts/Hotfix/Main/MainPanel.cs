using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using GameFrame.UI;
using UnityEngine;

namespace Game.Hotfix
{
    public partial class MainPanel : UIPanel<UINone>
    {
        private bool openingGame;

        protected override void OnCreate()
        {
            gomokuButton.onClick.AddListener(OpenGomoku);
            woodenFishButton.onClick.AddListener(OpenWoodenFish);
            LifetimeScope.Register(() => gomokuButton.onClick.RemoveListener(OpenGomoku));
            LifetimeScope.Register(() => woodenFishButton.onClick.RemoveListener(OpenWoodenFish));
        }

        protected override void OnOpen(UINone args)
        {
            openingGame = false;
            statusText.text = string.Empty;
            SetInteractable(true);
        }

        private void OpenGomoku()
        {
            OpenGameAsync<GomokuMainPanel>(OpenCancellationToken).Forget();
        }

        private void OpenWoodenFish()
        {
            OpenGameAsync<WoodenFishMainPanel>(OpenCancellationToken).Forget();
        }

        private async UniTask OpenGameAsync<TPanel>(CancellationToken token) where TPanel : UIPanel<UINone>
        {
            if (openingGame || token.IsCancellationRequested || !gameObject.activeInHierarchy)
            {
                return;
            }

            openingGame = true;
            SetInteractable(false);
            statusText.text = "正在打开…";
            try
            {
                await GameUI.Push<TPanel>();
                if (!token.IsCancellationRequested)
                {
                    statusText.text = string.Empty;
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested)
            {
            }
            catch (Exception error)
            {
                if (!token.IsCancellationRequested)
                {
                    statusText.text = "打开失败，请再次点击";
                }

                Debug.LogException(error);
            }
            finally
            {
                if (!token.IsCancellationRequested)
                {
                    openingGame = false;
                    SetInteractable(true);
                }
            }
        }

        private void SetInteractable(bool value)
        {
            gomokuButton.interactable = value;
            woodenFishButton.interactable = value;
        }
    }
}
