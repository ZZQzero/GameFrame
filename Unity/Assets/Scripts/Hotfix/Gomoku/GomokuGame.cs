using System;
using System.Collections.Generic;

namespace Game.Hotfix
{
    public enum GomokuStone
    {
        None,
        Black,
        White
    }

    public enum GomokuGameState
    {
        Playing,
        BlackWon,
        WhiteWon,
        Draw
    }

    public enum GomokuMoveResult
    {
        Accepted,
        GameFinished,
        WrongTurn,
        OutOfBounds,
        Occupied
    }

    public readonly struct GomokuMove
    {
        public int Column { get; }
        public int Row { get; }
        public GomokuStone Stone { get; }

        public GomokuMove(int column, int row, GomokuStone stone)
        {
            Column = column;
            Row = row;
            Stone = stone;
        }
    }

    /// <summary>自由五子棋：黑方先行，无禁手，连续五子及以上获胜。不依赖 Unity。</summary>
    public sealed class GomokuGame
    {
        readonly GomokuStone[,] board;
        readonly List<GomokuMove> moves = new List<GomokuMove>();

        public int Size { get; }
        public GomokuStone CurrentTurn { get; private set; } = GomokuStone.Black;
        public GomokuGameState State { get; private set; } = GomokuGameState.Playing;
        public IReadOnlyList<GomokuMove> Moves { get; }

        public GomokuGame(int size)
        {
            if (size < 2)
            {
                throw new ArgumentOutOfRangeException(nameof(size), size, "棋盘每个方向至少需要两个交点。");
            }

            Size = size;
            board = new GomokuStone[size, size];
            Moves = moves.AsReadOnly();
        }

        public GomokuStone GetStone(int column, int row)
        {
            if (!IsInside(column, row))
            {
                throw new ArgumentOutOfRangeException(nameof(column), "查询位置不在棋盘内。");
            }

            return board[column, row];
        }

        /// <summary>提交带执子方的操作。拒绝时不改变棋局，后续可用于应用服务器确认的落子。</summary>
        public GomokuMoveResult PlaceMove(int column, int row, GomokuStone stone)
        {
            if (stone != GomokuStone.Black && stone != GomokuStone.White)
            {
                throw new ArgumentOutOfRangeException(nameof(stone));
            }

            if (State != GomokuGameState.Playing)
            {
                return GomokuMoveResult.GameFinished;
            }

            if (stone != CurrentTurn)
            {
                return GomokuMoveResult.WrongTurn;
            }

            if (!IsInside(column, row))
            {
                return GomokuMoveResult.OutOfBounds;
            }

            if (board[column, row] != GomokuStone.None)
            {
                return GomokuMoveResult.Occupied;
            }

            board[column, row] = stone;
            moves.Add(new GomokuMove(column, row, stone));
            if (HasFive(column, row, stone))
            {
                State = stone == GomokuStone.Black ? GomokuGameState.BlackWon : GomokuGameState.WhiteWon;
            }
            else if (moves.Count == board.Length)
            {
                State = GomokuGameState.Draw;
            }
            else
            {
                CurrentTurn = stone == GomokuStone.Black ? GomokuStone.White : GomokuStone.Black;
            }

            return GomokuMoveResult.Accepted;
        }

        /// <summary>撤销最近一手；空棋局返回 false。终局撤销后恢复轮到被撤销的执子方。</summary>
        public bool UndoLastMove()
        {
            if (moves.Count == 0)
            {
                return false;
            }

            var index = moves.Count - 1;
            var move = moves[index];
            moves.RemoveAt(index);
            board[move.Column, move.Row] = GomokuStone.None;
            CurrentTurn = move.Stone;
            State = GomokuGameState.Playing;
            return true;
        }

        bool IsInside(int column, int row)
        {
            return column >= 0 && column < Size && row >= 0 && row < Size;
        }

        bool HasFive(int column, int row, GomokuStone stone)
        {
            return CountLine(column, row, 1, 0, stone) >= 5
                || CountLine(column, row, 0, 1, stone) >= 5
                || CountLine(column, row, 1, 1, stone) >= 5
                || CountLine(column, row, 1, -1, stone) >= 5;
        }

        int CountLine(int column, int row, int dx, int dy, GomokuStone stone)
        {
            return 1 + CountDirection(column, row, dx, dy, stone)
                + CountDirection(column, row, -dx, -dy, stone);
        }

        int CountDirection(int column, int row, int dx, int dy, GomokuStone stone)
        {
            var count = 0;
            column += dx;
            row += dy;
            while (IsInside(column, row) && board[column, row] == stone)
            {
                count++;
                column += dx;
                row += dy;
            }

            return count;
        }
    }
}
