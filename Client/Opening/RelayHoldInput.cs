namespace ContrabandCases.Client.Opening;

/// <summary>Owns deliberate input edges; the existing timer owns one-shot confirmation.</summary>
internal sealed class RelayHoldInput
{
    private readonly RelayHoldConfirmation _hold = new();
    private bool _pointerHeld;
    private bool _submitHeld;
    private bool _awaitSubmitRelease = true;

    public double Progress => _hold.Progress;

    public void PointerDown(bool primary, bool focused)
    {
        if (!primary || !focused) return;
        _pointerHeld = true;
        _hold.Begin();
    }

    public void PointerUp(bool primary)
    {
        if (!primary) return;
        _pointerHeld = false;
        if (!_submitHeld) _hold.Cancel();
    }

    public void Interrupt()
    {
        _pointerHeld = false;
        _submitHeld = false;
        _awaitSubmitRelease = true;
        _hold.Cancel();
    }

    public bool Advance(double seconds, bool canInteract, bool selected, bool submitPressed)
    {
        if (!canInteract)
        {
            Interrupt();
            return false;
        }
        if (!submitPressed) _awaitSubmitRelease = false;
        var held = selected && submitPressed && !_awaitSubmitRelease;
        if (held && !_submitHeld) _hold.Begin();
        if (!held && _submitHeld && !_pointerHeld) _hold.Cancel();
        // Moving keyboard focus onto this control while Submit is held is not a fresh press.
        if (!selected && submitPressed) _awaitSubmitRelease = true;
        _submitHeld = held;
        return (_pointerHeld || _submitHeld) && _hold.Advance(seconds);
    }
}
