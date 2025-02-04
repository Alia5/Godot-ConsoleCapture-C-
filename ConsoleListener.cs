using Godot;

[GlobalClass]
public partial class ConsoleListener : Node {

    [Signal]
    public delegate void StdOutEventHandler(string str);
    [Signal]
    public delegate void StdErrEventHandler(string str);

    public override void _EnterTree() {
        if (!ConsoleCapture.IsInitialized) {
            ConsoleCapture.Initialize();
        }
        ConsoleCapture.AddListener(this);
        base._EnterTree();
    }

    public override void _ExitTree() {
        ConsoleCapture.RemoveListener(this);
        base._ExitTree();
    }

    public void EmitStdOut(string str) {
        EmitSignal(SignalName.StdOut, str);
    }
    public void EmitStdErr(string str) {
        EmitSignal(SignalName.StdErr, str);
    }
}
