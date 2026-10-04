using Xunit;

namespace KSAStructures.Tests;

public class HeadLookTests
{
    private static HeadLook Dragged(double dx, double dy)
    {
        var look = new HeadLook();
        look.Move(500, 500);
        look.Press();
        look.Move(500 + (dx / 2), 500 + (dy / 2));
        look.Move(500 + dx, 500 + dy);
        return look;
    }

    [Fact]
    public void DraggingRightLooksRightAndUpLooksUp()
    {
        HeadLook look = Dragged(100, -100);

        Assert.True(look.Yaw > 0.0);
        Assert.True(look.Pitch > 0.0);
    }

    [Fact]
    public void MovingWithoutTheButtonLooksNowhere()
    {
        var look = new HeadLook();
        look.Move(0, 0);
        look.Move(300, 300);

        Assert.Equal(0.0, look.Yaw);
        Assert.Equal(0.0, look.Pitch);
    }

    [Fact]
    public void TheLookStaysWhereItWasPut()
    {
        HeadLook look = Dragged(200, 0);
        double yaw = look.Yaw;
        look.Release();
        look.Move(900, 900);

        Assert.Equal(yaw, look.Yaw);
    }

    [Fact]
    public void ItCannotLookPastStraightUp()
    {
        Assert.Equal(HeadLook.MaxPitchRad, Dragged(0, -100_000).Pitch);
    }

    [Fact]
    public void TheCapturedMouseTurnsTheViewWithNoButton()
    {
        var look = new HeadLook();
        look.Turn(100, -50);

        Assert.True(look.Yaw > 0.0 && look.Pitch > 0.0);
        Assert.True(Math.Abs(look.Yaw - (2.0 * look.Pitch)) < 1e-12);
    }
}
