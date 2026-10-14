using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using GameFrame.UI;
using UnityEngine;

namespace Game.Hotfix
{
    public sealed class MainPanelArgs
    {
        public Func<UniTask> OpenGomoku { get; }
        public string Message { get; }

        public MainPanelArgs(Func<UniTask> openGomoku, string message = "")
        {
            OpenGomoku = openGomoku ?? throw new ArgumentNullException(nameof(openGomoku));
            Message = message;
        }
    }

    public partial class MainPanel : UIPanel<MainPanelArgs>
    {
        private bool openingGame;
        private Func<UniTask> openGomoku;

        protected override void OnCreate()
        {
            gomokuButton.onClick.AddListener(OpenGomoku);
            woodenFishButton.onClick.AddListener(OpenWoodenFish);
            settingsButton.onClick.AddListener(OpenSettings);
            LifetimeScope.Register(() => gomokuButton.onClick.RemoveListener(OpenGomoku));
            LifetimeScope.Register(() => woodenFishButton.onClick.RemoveListener(OpenWoodenFish));
            LifetimeScope.Register(() => settingsButton.onClick.RemoveListener(OpenSettings));
        }

        protected override void OnOpen(MainPanelArgs args)
        {
            openGomoku = args.OpenGomoku;
            openingGame = false;
            statusText.text = args.Message;
            SetInteractable(true);
        }

        private void OpenGomoku()
        {
            OpenGameAsync(openGomoku, OpenCancellationToken).Forget();
        }

        private void OpenWoodenFish()
        {
            OpenGameAsync(async () =>
            {
                await GameUI.Push<WoodenFishMainPanel>();
            }, OpenCancellationToken).Forget();
        }

        private void OpenSettings()
        {
            OpenGameAsync(async () =>
            {
                await GameUI.Push<SettingsPanel>();
            }, OpenCancellationToken).Forget();
        }

        private async UniTask OpenGameAsync(Func<UniTask> open, CancellationToken token)
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
                await open();
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
            settingsButton.interactable = value;
        }
    }
}
