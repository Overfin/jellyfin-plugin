using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace Overfin.Jellyfin;

/// <summary>Why a run of ffmpeg gave nothing.</summary>
internal enum RunFailure
{
    None,

    /// <summary>It took longer than its time limit.</summary>
    TooSlow,

    /// <summary>It wrote more than its size limit.</summary>
    TooLarge,

    /// <summary>It ended with an error.</summary>
    Failed,
}

/// <summary>
/// Runs Jellyfin's ffmpeg with fixed arguments, no shell, a few at once,
/// each within a time and a size limit (SECURITY.md rule 6).
/// </summary>
internal static class Ffmpeg
{
    /// <summary>How many run at once; others wait for their turn.</summary>
    private static readonly SemaphoreSlim Slots = new(4);

    /// <summary>How long one run may take.</summary>
    public static readonly TimeSpan TimeLimit = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Runs ffmpeg and gives what it wrote to its standard output, or why it
    /// gave nothing. Its own messages are read and dropped.
    /// </summary>
    public static async Task<(byte[] Output, RunFailure Failure)> Run(string ffmpeg, IReadOnlyList<string> args, int maxBytes, CancellationToken cancel)
    {
        await Slots.WaitAsync(cancel).ConfigureAwait(false);
        try
        {
            var start = new ProcessStartInfo(ffmpeg)
            {
                UseShellExecute = false,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var a in args)
            {
                start.ArgumentList.Add(a);
            }

            using var process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg didn't start");
            process.StandardInput.Close();
            using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
            limit.CancelAfter(TimeLimit);
            var errors = process.StandardError.BaseStream.CopyToAsync(Stream.Null, limit.Token);
            try
            {
                var output = new MemoryStream();
                var buffer = new byte[81920];
                int n;
                while ((n = await process.StandardOutput.BaseStream.ReadAsync(buffer, limit.Token).ConfigureAwait(false)) > 0)
                {
                    if (output.Length + n > maxBytes)
                    {
                        Kill(process);
                        return ([], RunFailure.TooLarge);
                    }

                    output.Write(buffer, 0, n);
                }

                await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
                await errors.ConfigureAwait(false);
                return process.ExitCode == 0 ? (output.ToArray(), RunFailure.None) : ([], RunFailure.Failed);
            }
            catch (OperationCanceledException)
            {
                Kill(process);
                cancel.ThrowIfCancellationRequested();
                return ([], RunFailure.TooSlow);
            }
        }
        finally
        {
            Slots.Release();
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // It had ended already.
        }
    }
}
