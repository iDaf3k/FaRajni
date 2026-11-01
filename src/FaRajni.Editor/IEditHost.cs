using System.Windows;

namespace FaRajni.Editor;

/// <summary>What the editor needs from the player that hosts it.</summary>
public interface IEditHost
{
    string FilePath { get; }
    string Title { get; }
    long LengthMs { get; }
    long PositionMs { get; }
    bool IsPlaying { get; }
    void Seek(long ms);
    void SetPlaying(bool playing);
    Window Owner { get; }
    void CloseEditor();
    /// <summary>An export finished; <paramref name="outputPath"/> sits next to the original.</summary>
    void Exported(string outputPath, string message);
    void Toast(string message);
}
