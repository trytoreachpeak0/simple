namespace Simple.OnboardTerminal.Core.Tests;

public sealed class OnboardInputsTests
{
    private static OnboardInputs Connected()
    {
        var inputs = new OnboardInputs();
        inputs.Open();
        return inputs;
    }

    [Fact]
    public void AScanIsWhatWasTypedBeforeEnterAndMayArriveInPieces()
    {
        var inputs = Connected();

        Assert.Empty(inputs.Type("LOT-")!);
        var scans = inputs.Type("0001\r\n")!;

        Assert.Equal("LOT-0001", Assert.Single(scans).Text);
    }

    [Fact]
    public void EveryEnterEndsOneScanAndAnEmptyEnterIsNone()
    {
        var scans = Connected().Type("A1\rB2\n\r\nC3\r")!;

        Assert.Equal(["A1", "B2", "C3"], scans.Select(s => s.Text));
        Assert.Equal([1UL, 2UL, 3UL], scans.Select(s => s.Id));
    }

    [Fact]
    public void ScanAndPressGoIntoEveryReportUntilAReportCarryingThemIsAcknowledged()
    {
        var inputs = Connected();
        inputs.Type("LOT-0001\r");
        Assert.Equal(1UL, Assert.Single(inputs.ForReport(5)).Id);
        Assert.Equal(2UL, inputs.Press("confirm"));

        // Report 6 carries both; the server has seen up to report 5.
        var sixth = inputs.ForReport(6);
        Assert.Equal(["LOT-0001", ""], sixth.Select(i => i.Scan?.Text ?? string.Empty));
        Assert.Equal("confirm", sixth[1].ButtonPress.Button);
        inputs.Acknowledge(5);

        Assert.Equal(2UL, Assert.Single(inputs.ForReport(7)).Id);
        inputs.Acknowledge(6);
        Assert.Empty(inputs.ForReport(8));
    }

    [Fact]
    public void NothingIsTakenInWhileDisconnectedAndNothingCrossesToTheNextStream()
    {
        var inputs = new OnboardInputs();
        Assert.Null(inputs.Type("LOT-0001\r"));
        Assert.Null(inputs.Press("confirm"));

        inputs.Open();
        inputs.Type("LOT-");
        inputs.Press("confirm");
        inputs.Close();
        inputs.Open();

        Assert.Empty(inputs.ForReport(1));
        // The half-typed scan did not survive either.
        Assert.Equal("0002", Assert.Single(inputs.Type("0002\r")!).Text);
    }
}
