using System.Text;
using System.Text.RegularExpressions;

namespace ClaudeMobileTerminal.Backend.Services;

/// <summary>
/// Simple terminal emulator that processes control characters to maintain screen state
/// This allows us to capture what the user would see, not the raw output stream
/// </summary>
public class TerminalEmulator
{
    private readonly List<StringBuilder> _screenBuffer = new();
    private int _cursorRow = 0;
    private int _cursorColumn = 0;
    private const int MaxRows = 100;
    private const int MaxColumns = 200;
    
    // ANSI escape sequence patterns
    private static readonly Regex AnsiEscapeRegex = new(@"\x1B\[([0-9;]+)?([A-Za-z])", RegexOptions.Compiled);
    private static readonly Regex CsiRegex = new(@"\x1B\[([0-9;]*)?([A-Za-z])", RegexOptions.Compiled);
    
    public TerminalEmulator()
    {
        // Initialize with one empty line
        _screenBuffer.Add(new StringBuilder());
    }
    
    /// <summary>
    /// Process terminal output and update screen buffer
    /// </summary>
    public void ProcessOutput(string output)
    {
        int i = 0;
        while (i < output.Length)
        {
            // Check for ANSI escape sequences
            if (i < output.Length - 1 && output[i] == '\x1B')
            {
                var match = AnsiEscapeRegex.Match(output, i);
                if (match.Success)
                {
                    ProcessAnsiSequence(match);
                    i = match.Index + match.Length;
                    continue;
                }
            }
            
            // Process regular characters
            char ch = output[i];
            switch (ch)
            {
                case '\r': // Carriage return - move to beginning of line
                    _cursorColumn = 0;
                    break;
                    
                case '\n': // Line feed - move to next line
                    _cursorRow++;
                    EnsureRowExists(_cursorRow);
                    // Don't reset column on LF (Unix style)
                    break;
                    
                case '\b': // Backspace
                    if (_cursorColumn > 0)
                        _cursorColumn--;
                    break;
                    
                case '\t': // Tab
                    _cursorColumn = (_cursorColumn + 8) & ~7; // Move to next tab stop
                    break;
                    
                default:
                    // Regular printable character
                    if (ch >= 32) // Printable ASCII
                    {
                        EnsureRowExists(_cursorRow);
                        var line = _screenBuffer[_cursorRow];
                        
                        // Ensure line is long enough
                        while (line.Length <= _cursorColumn)
                            line.Append(' ');
                        
                        // Overwrite character at cursor position
                        if (_cursorColumn < line.Length)
                            line[_cursorColumn] = ch;
                        else
                            line.Append(ch);
                        
                        _cursorColumn++;
                    }
                    break;
            }
            i++;
        }
    }
    
    private void ProcessAnsiSequence(Match match)
    {
        string parameters = match.Groups[1].Value;
        char command = match.Groups[2].Value[0];
        
        switch (command)
        {
            case 'K': // Erase in line
                EraseLine(parameters);
                break;
                
            case 'J': // Erase in display
                EraseDisplay(parameters);
                break;
                
            case 'A': // Cursor up
            case 'B': // Cursor down
            case 'C': // Cursor forward
            case 'D': // Cursor back
                MoveCursor(command, parameters);
                break;
                
            case 'H': // Cursor position
            case 'f': // Cursor position (alternate)
                SetCursorPosition(parameters);
                break;
                
            // Ignore other sequences for now
        }
    }
    
    private void EraseLine(string parameters)
    {
        if (_cursorRow >= _screenBuffer.Count) return;
        
        var line = _screenBuffer[_cursorRow];
        int mode = string.IsNullOrEmpty(parameters) ? 0 : int.Parse(parameters);
        
        switch (mode)
        {
            case 0: // Clear from cursor to end of line
                if (_cursorColumn < line.Length)
                    line.Remove(_cursorColumn, line.Length - _cursorColumn);
                break;
                
            case 1: // Clear from beginning to cursor
                for (int i = 0; i <= _cursorColumn && i < line.Length; i++)
                    line[i] = ' ';
                break;
                
            case 2: // Clear entire line
                line.Clear();
                break;
        }
    }
    
    private void EraseDisplay(string parameters)
    {
        int mode = string.IsNullOrEmpty(parameters) ? 0 : int.Parse(parameters);
        
        switch (mode)
        {
            case 0: // Clear from cursor to end
                // Clear rest of current line
                if (_cursorRow < _screenBuffer.Count)
                {
                    var line = _screenBuffer[_cursorRow];
                    if (_cursorColumn < line.Length)
                        line.Remove(_cursorColumn, line.Length - _cursorColumn);
                }
                // Clear all lines below
                while (_screenBuffer.Count > _cursorRow + 1)
                    _screenBuffer.RemoveAt(_screenBuffer.Count - 1);
                break;
                
            case 2: // Clear entire screen
                _screenBuffer.Clear();
                _screenBuffer.Add(new StringBuilder());
                _cursorRow = 0;
                _cursorColumn = 0;
                break;
        }
    }
    
    private void MoveCursor(char direction, string parameters)
    {
        int count = string.IsNullOrEmpty(parameters) ? 1 : int.Parse(parameters);
        
        switch (direction)
        {
            case 'A': // Up
                _cursorRow = Math.Max(0, _cursorRow - count);
                break;
            case 'B': // Down
                _cursorRow = Math.Min(MaxRows - 1, _cursorRow + count);
                EnsureRowExists(_cursorRow);
                break;
            case 'C': // Forward
                _cursorColumn = Math.Min(MaxColumns - 1, _cursorColumn + count);
                break;
            case 'D': // Back
                _cursorColumn = Math.Max(0, _cursorColumn - count);
                break;
        }
    }
    
    private void SetCursorPosition(string parameters)
    {
        var parts = parameters.Split(';');
        int row = parts.Length > 0 && !string.IsNullOrEmpty(parts[0]) ? int.Parse(parts[0]) - 1 : 0;
        int col = parts.Length > 1 && !string.IsNullOrEmpty(parts[1]) ? int.Parse(parts[1]) - 1 : 0;
        
        _cursorRow = Math.Max(0, Math.Min(MaxRows - 1, row));
        _cursorColumn = Math.Max(0, Math.Min(MaxColumns - 1, col));
        EnsureRowExists(_cursorRow);
    }
    
    private void EnsureRowExists(int row)
    {
        while (_screenBuffer.Count <= row && _screenBuffer.Count < MaxRows)
        {
            _screenBuffer.Add(new StringBuilder());
        }
    }
    
    /// <summary>
    /// Get the current screen content as it would appear to the user
    /// </summary>
    public string GetScreenContent()
    {
        var result = new StringBuilder();
        
        // Find the last non-empty line
        int lastNonEmptyLine = -1;
        for (int i = _screenBuffer.Count - 1; i >= 0; i--)
        {
            if (_screenBuffer[i].Length > 0 && _screenBuffer[i].ToString().Trim().Length > 0)
            {
                lastNonEmptyLine = i;
                break;
            }
        }
        
        // Build output up to last non-empty line
        for (int i = 0; i <= lastNonEmptyLine; i++)
        {
            if (i > 0) result.AppendLine();
            result.Append(_screenBuffer[i].ToString().TrimEnd());
        }
        
        return result.ToString();
    }
    
    /// <summary>
    /// Clear the screen buffer
    /// </summary>
    public void Clear()
    {
        _screenBuffer.Clear();
        _screenBuffer.Add(new StringBuilder());
        _cursorRow = 0;
        _cursorColumn = 0;
    }
}