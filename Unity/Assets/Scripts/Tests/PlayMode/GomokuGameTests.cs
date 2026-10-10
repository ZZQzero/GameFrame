using System;
using Game.Hotfix;
using NUnit.Framework;

namespace GameFrame.Tests
{
    public sealed class GomokuGameTests
    {
        [TestCase(15)]
        [TestCase(18)]
        [TestCase(20)]
        public void TurnsAndRejectedMovesPreserveState(int size)
        {
            var game = new GomokuGame(size);
            Assert.AreEqual(GomokuStone.Black, game.CurrentTurn);
            Assert.AreEqual(GomokuMoveResult.WrongTurn, game.PlaceMove(0, 0, GomokuStone.White));
            Assert.AreEqual(GomokuMoveResult.OutOfBounds, game.PlaceMove(size, 0, GomokuStone.Black));
            Assert.AreEqual(GomokuMoveResult.OutOfBounds, game.PlaceMove(0, -1, GomokuStone.Black));
            Assert.AreEqual(0, game.Moves.Count);
            Assert.AreEqual(GomokuMoveResult.Accepted, game.PlaceMove(size - 1, size - 1, GomokuStone.Black));
            Assert.AreEqual(GomokuStone.White, game.CurrentTurn);
            Assert.AreEqual(GomokuMoveResult.Occupied, game.PlaceMove(size - 1, size - 1, GomokuStone.White));
            Assert.AreEqual(GomokuStone.White, game.CurrentTurn);
            Assert.AreEqual(1, game.Moves.Count);
            Assert.AreEqual(GomokuStone.Black, game.GetStone(size - 1, size - 1));
        }

        [TestCase(1, 0)]
        [TestCase(0, 1)]
        [TestCase(1, 1)]
        [TestCase(1, -1)]
        public void FiveWinsInEveryDirectionAndUndoReopensGame(int dx, int dy)
        {
            var game = new GomokuGame(20);
            for (var index = 0; index < 5; index++)
            {
                Assert.AreEqual(GomokuMoveResult.Accepted, game.PlaceMove(2 + index * dx, 6 + index * dy, GomokuStone.Black));
                if (index < 4)
                {
                    Assert.AreEqual(GomokuGameState.Playing, game.State);
                    Assert.AreEqual(GomokuMoveResult.Accepted, game.PlaceMove(index * 2, 19, GomokuStone.White));
                }
            }

            Assert.AreEqual(GomokuGameState.BlackWon, game.State);
            Assert.AreEqual(GomokuMoveResult.GameFinished, game.PlaceMove(19, 0, GomokuStone.White));
            Assert.AreEqual(9, game.Moves.Count);
            Assert.IsTrue(game.UndoLastMove());
            Assert.AreEqual(GomokuGameState.Playing, game.State);
            Assert.AreEqual(GomokuStone.Black, game.CurrentTurn);
            Assert.AreEqual(GomokuStone.None, game.GetStone(2 + 4 * dx, 6 + 4 * dy));
            Assert.AreEqual(GomokuMoveResult.Accepted, game.PlaceMove(2 + 4 * dx, 6 + 4 * dy, GomokuStone.Black));
            Assert.AreEqual(GomokuGameState.BlackWon, game.State);
        }

        [Test]
        public void WhiteCanWin()
        {
            var game = new GomokuGame(20);
            for (var index = 0; index < 5; index++)
            {
                game.PlaceMove(index * 2, 19, GomokuStone.Black);
                game.PlaceMove(index, 0, GomokuStone.White);
            }

            Assert.AreEqual(GomokuGameState.WhiteWon, game.State);
        }

        [Test]
        public void FillingGapCountsBothSidesAndAllowsOverline()
        {
            var game = new GomokuGame(20);
            var columns = new[] { 0, 1, 3, 4, 5, 2 };
            for (var index = 0; index < columns.Length; index++)
            {
                Assert.AreEqual(GomokuMoveResult.Accepted, game.PlaceMove(columns[index], 0, GomokuStone.Black));
                if (index < columns.Length - 1)
                {
                    Assert.AreEqual(GomokuGameState.Playing, game.State);
                    game.PlaceMove(index * 2, 19, GomokuStone.White);
                }
            }

            Assert.AreEqual(GomokuGameState.BlackWon, game.State);
        }

        [Test]
        public void FullBoardDrawCanBeUndoneAndReplayed()
        {
            var game = new GomokuGame(3);
            for (var row = 0; row < game.Size; row++)
            {
                for (var column = 0; column < game.Size; column++)
                {
                    Assert.AreEqual(GomokuMoveResult.Accepted, game.PlaceMove(column, row, game.CurrentTurn));
                }
            }

            Assert.AreEqual(GomokuGameState.Draw, game.State);
            var restored = new GomokuGame(game.Size);
            foreach (var move in game.Moves)
            {
                Assert.AreEqual(GomokuMoveResult.Accepted, restored.PlaceMove(move.Column, move.Row, move.Stone));
            }

            Assert.AreEqual(game.State, restored.State);
            Assert.AreEqual(game.Moves.Count, restored.Moves.Count);
            Assert.IsTrue(game.UndoLastMove());
            Assert.AreEqual(GomokuGameState.Playing, game.State);
            Assert.AreEqual(GomokuStone.None, game.GetStone(2, 2));
            while (game.UndoLastMove())
            {
            }

            Assert.AreEqual(GomokuStone.Black, game.CurrentTurn);
            Assert.AreEqual(0, game.Moves.Count);
        }

        [Test]
        public void InvalidConfigurationAndStoneThrowBeforeMutation()
        {
            Assert.Throws<ArgumentOutOfRangeException>(() => new GomokuGame(1));
            var game = new GomokuGame(15);
            Assert.Throws<ArgumentOutOfRangeException>(() => game.PlaceMove(0, 0, GomokuStone.None));
            Assert.Throws<ArgumentOutOfRangeException>(() => game.GetStone(-1, 0));
            Assert.AreEqual(0, game.Moves.Count);
            Assert.AreEqual(GomokuStone.Black, game.CurrentTurn);
        }
    }
}
