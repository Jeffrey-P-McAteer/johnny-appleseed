namespace JohnnyAppleseed.UI;

/// <summary>
/// Reveals a string one character at a time, like a typewriter telling a story.
///
/// The reveal is paced by <see cref="CharsPerSecond"/>, with longer pauses after
/// sentence- and clause-ending punctuation so the cadence reads naturally rather
/// than mechanically. Operating on the raw (un-wrapped) text means the caller can
/// re-wrap the visible substring every frame — the reveal survives window resizes.
/// </summary>
sealed class Typewriter
{
    private string _text = "";
    private int _index;      // number of characters currently revealed
    private float _timer;    // seconds until the next character appears

    /// <summary>Baseline reveal speed, before per-character punctuation pauses.</summary>
    public float CharsPerSecond { get; set; } = 42f;

    /// <summary>Begin revealing a new string from the first character.</summary>
    public void SetText(string text)
    {
        _text = text ?? "";
        _index = 0;
        _timer = 0f;
    }

    /// <summary>The prefix of the text revealed so far.</summary>
    public string Visible => _text[.._index];

    public string FullText => _text;

    public bool IsComplete => _index >= _text.Length;

    /// <summary>Reveal everything immediately (player pressed continue while typing).</summary>
    public void CompleteNow() => _index = _text.Length;

    public void Update(float dt)
    {
        if (IsComplete)
            return;

        _timer -= dt;

        // Reveal as many characters as the elapsed time allows. The guard stops a
        // pathological spike (e.g. a huge dt after a stall) from locking the loop.
        int guard = 0;
        while (_timer <= 0f && _index < _text.Length && guard++ < 1024)
        {
            char c = _text[_index++];
            _timer += DelayAfter(c);
        }
    }

    // How long to wait after emitting a given character before the next appears.
    private float DelayAfter(char c)
    {
        float baseDelay = 1f / MathF.Max(1f, CharsPerSecond);

        return c switch
        {
            '\n'                              => baseDelay * 0.25f, // newlines feel instant
            '.' or '!' or '?'                 => baseDelay * 9f,    // full stop — long beat
            ',' or ';' or ':' or '—'     => baseDelay * 4f,    // comma / em-dash — short beat
            _                                 => baseDelay,
        };
    }
}
