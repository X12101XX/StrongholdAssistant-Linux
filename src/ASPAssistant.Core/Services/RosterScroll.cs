namespace ASPAssistant.Core.Services;

/// <summary>按当前网格留两列画面重叠，并用同名卡的实际位移校准拖动比例。</summary>
public sealed class RosterScroll
{
    private RosterReading? _previous;
    private int _drag;
    private bool _calibrated;
    private double _movementPerDrag = 1;

    public int NextDrag(byte[] frame, RosterReading reading)
    {
        if (_previous is not null)
        {
            var shifts = (from before in _previous.Cards
                          join after in reading.Cards on before.Operator.Name equals after.Operator.Name
                          select Math.Abs(before.X - after.X)).Order().ToArray();
            if (shifts.Length > 0 && shifts[shifts.Length / 2] > 0)
            {
                // 到头的短位移不能放大后续拖动；始终保留已测得的最大比例。
                _movementPerDrag = Math.Max(_movementPerDrag, shifts[shifts.Length / 2] / (double)_drag);
                _calibrated = true;
            }
        }
        var (width, spacing) = RosterReader.MeasureScroll(frame);
        // 每个方向首次只走一列，保留足够的同名卡测量实际位移。
        double movement = _calibrated ? width - 2 * spacing : spacing;
        _previous = reading;
        return _drag = (int)Math.Floor(movement / _movementPerDrag);
    }
}
