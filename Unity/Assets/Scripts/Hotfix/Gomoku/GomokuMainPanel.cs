using System;
using Cysharp.Threading.Tasks;
using GameFrame.Audio;
using GameFrame.UI;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

namespace Game.Hotfix
{
    public partial class GomokuMainPanel : UIPanel<UINone>
    {
        [SerializeField] GomokuBoardView boardView;
        [SerializeField] TextMeshProUGUI statusText;
        [SerializeField] Button restartButton;
        [SerializeField] Button undoButton;

        public GomokuGame Game { get; private set; }

        protected override void OnCreate()
        {
            boardView.IntersectionClicked += OnIntersectionClicked;
            restartButton.onClick.AddListener(RestartGame);
            undoButton.onClick.AddListener(UndoMove);
            LifetimeScope.Register(() => boardView.IntersectionClicked -= OnIntersectionClicked);
            LifetimeScope.Register(() => restartButton.onClick.RemoveListener(RestartGame));
            LifetimeScope.Register(() => undoButton.onClick.RemoveListener(UndoMove));
        }

        protected override void OnOpen(UINone args)
        {
            if (Game == null)
            {
                StartNewGame(boardView.Grid.PointCount);
            }
            else
            {
                RefreshGame();
            }

            boardView.Interactable = true;
            OpenScope.Register(() => boardView.Interactable = false);
        }

        protected override void OnPause()
        {
            boardView.Interactable = false;
        }

        protected override void OnResume()
        {
            boardView.Interactable = true;
        }

        public void StartNewGame(int pointCount)
        {
            var game = new GomokuGame(pointCount);
            boardView.Grid.SetPointCount(pointCount);
            Game = game;
            RefreshGame();
        }

        /// <summary>本地确认落子并更新显示；网络接入后可由服务器确认消息调用。</summary>
        public GomokuMoveResult ApplyMove(int column, int row, GomokuStone stone)
        {
            if (Game == null)
            {
                throw new InvalidOperationException("[Gomoku] 请先开始棋局。");
            }

            var result = Game.PlaceMove(column, row, stone);
            if (result == GomokuMoveResult.Accepted)
            {
                RefreshGame();
                GameAudio.TryPlayAsync(GameAudioIds.GomokuPlace, OpenCancellationToken).Forget();
            }

            return result;
        }

        void OnIntersectionClicked(int column, int row)
        {
            ApplyMove(column, row, Game.CurrentTurn);
        }

        void RestartGame()
        {
            StartNewGame(boardView.Grid.PointCount);
        }

        void UndoMove()
        {
            if (Game.UndoLastMove())
            {
                RefreshGame();
            }
        }

        void RefreshGame()
        {
            boardView.Render(Game);
            undoButton.interactable = Game.Moves.Count > 0;
            switch (Game.State)
            {
                case GomokuGameState.BlackWon:
                    statusText.text = $"黑方获胜 · 共 {Game.Moves.Count} 手";
                    break;
                case GomokuGameState.WhiteWon:
                    statusText.text = $"白方获胜 · 共 {Game.Moves.Count} 手";
                    break;
                case GomokuGameState.Draw:
                    statusText.text = "棋盘已满 · 和棋";
                    break;
                default:
                    var player = Game.CurrentTurn == GomokuStone.Black ? "黑方" : "白方";
                    statusText.text = $"{player}回合 · 第 {Game.Moves.Count + 1} 手";
                    break;
            }
        }
    }
}
