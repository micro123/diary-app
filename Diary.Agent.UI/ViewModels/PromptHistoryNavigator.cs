namespace Diary.Agent.UI.ViewModels;

internal sealed class PromptHistoryNavigator(int capacity = 100)
{
    private readonly int _capacity = capacity > 0
        ? capacity
        : throw new ArgumentOutOfRangeException(nameof(capacity));
    private readonly List<string> _items = [];
    private int _index;
    private string _draft = string.Empty;

    public void Record(string prompt)
    {
        if (string.IsNullOrWhiteSpace(prompt))
            return;
        if (_items.Count == 0 || !string.Equals(_items[^1], prompt, StringComparison.Ordinal))
        {
            _items.Add(prompt);
            if (_items.Count > _capacity)
                _items.RemoveAt(0);
        }
        _index = _items.Count;
        _draft = string.Empty;
    }

    public void UpdateDraft(string draft)
    {
        _index = _items.Count;
        _draft = draft;
    }

    public bool TryMovePrevious(string currentInput, out string value)
    {
        if (_items.Count == 0 || _index == 0)
        {
            value = currentInput;
            return false;
        }
        if (_index == _items.Count)
            _draft = currentInput;
        _index--;
        value = _items[_index];
        return true;
    }

    public bool TryMoveNext(string currentInput, out string value)
    {
        if (_index >= _items.Count)
        {
            value = currentInput;
            return false;
        }
        _index++;
        value = _index == _items.Count ? _draft : _items[_index];
        return true;
    }
}
