using System.Collections.Generic;
using System.IO;

namespace FaRajni.Core.Scanning;

public class FolderScanner
{
    /// <summary>
    /// Yields every video file under <paramref name="root"/>, including nested subfolders.
    /// Walks the tree manually (rather than EnumerateFiles with AllDirectories) so that a
    /// single unreadable/protected subfolder is skipped instead of aborting the whole scan.
    /// </summary>
    public IEnumerable<string> FindVideos(string root)
    {
        if (!Directory.Exists(root))
            yield break;

        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var dir = stack.Pop();

            // Files in this directory. Skip the directory on any access/IO problem.
            string[] files;
            try { files = Directory.GetFiles(dir); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var file in files)
                if (VideoExtensions.IsVideo(file))
                    yield return file;

            // Queue subdirectories, again tolerating inaccessible ones.
            string[] subdirs;
            try { subdirs = Directory.GetDirectories(dir); }
            catch (IOException) { continue; }
            catch (UnauthorizedAccessException) { continue; }

            foreach (var sub in subdirs)
                stack.Push(sub);
        }
    }
}
