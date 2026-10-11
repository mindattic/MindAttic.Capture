using System.Diagnostics;
using System.Text;

#if !NET
namespace System.Runtime.CompilerServices
{
    internal static class IsExternalInit;
}
#endif

namespace MindAttic.Capture.Internal
{
    internal static class Compat
    {
        /// <summary>True when <paramref name="task"/> finished within <paramref name="timeout"/>.</summary>
        public static async Task<bool> CompletesWithin(this Task task, TimeSpan timeout)
        {
            if (task.IsCompleted) return true;
            var done = await Task.WhenAny(task, Task.Delay(timeout)).ConfigureAwait(false);
            return done == task;
        }

        public static Task WaitForExitCompatAsync(this Process process, CancellationToken ct = default)
        {
#if NET
            return process.WaitForExitAsync(ct);
#else
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => tcs.TrySetResult(true);
            if (process.HasExited) tcs.TrySetResult(true);
            if (ct.CanBeCanceled) ct.Register(() => tcs.TrySetCanceled());
            return tcs.Task.ContinueWith(t => { if (!t.IsCanceled) process.WaitForExit(); return t; }, TaskScheduler.Default).Unwrap();
#endif
        }

        /// <summary>
        /// True once the process itself has exited, within <paramref name="timeout"/>. Unlike WaitForExit(),
        /// it doesn't wait for EOF on redirected output, which a surviving child process can hold open.
        /// </summary>
        public static Task<bool> WaitForProcessExitAsync(this Process process, TimeSpan timeout)
        {
            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => tcs.TrySetResult(true);
            if (process.HasExited) tcs.TrySetResult(true);
            return WaitAsync(tcs.Task, timeout);

            static async Task<bool> WaitAsync(Task<bool> exited, TimeSpan timeout) =>
                await exited.CompletesWithin(timeout).ConfigureAwait(false) || exited.IsCompleted;
        }

        public static void AddArguments(this ProcessStartInfo psi, IEnumerable<string> args)
        {
#if NET
            foreach (var a in args) psi.ArgumentList.Add(a);
#else
            psi.Arguments = string.Join(" ", args.Select(QuoteWindowsArg));
#endif
        }

        /// <summary>Quotes one argument for CommandLineToArgvW / MSVCRT parsing.</summary>
        internal static string QuoteWindowsArg(string arg)
        {
            if (arg.Length > 0 && arg.IndexOfAny([' ', '\t', '"', '\n', '\v']) < 0) return arg;
            var sb = new StringBuilder("\"");
            var slashes = 0;
            foreach (var c in arg)
            {
                if (c == '\\') { slashes++; continue; }
                if (c == '"') sb.Append('\\', slashes * 2 + 1);
                else sb.Append('\\', slashes);
                slashes = 0;
                sb.Append(c);
            }
            sb.Append('\\', slashes * 2).Append('"');
            return sb.ToString();
        }
    }
}
