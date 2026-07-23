namespace Incode
{
    using System;

    internal enum Command
    {
        Up = 1,
        Down = 2,
        Left = 4,
        Right = 8,
        ScrollUp,
        ScrollDown,
        LeftDown,
        RightDown,
        ScrollUpAmount,
        ScrollDownAmount
    }
}
