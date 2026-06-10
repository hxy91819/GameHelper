using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading;
using Spectre.Console;

namespace GameHelper.ConsoleHost.Interactive
{
    /// <summary>
    /// 封装交互式提示的基础 UI 设施：选择框、文本输入、确认对话框、数字键盘快捷选择。
    /// </summary>
    public sealed class PromptUI
    {
        private const int DirectNumberPollingMilliseconds = 25;
        private static readonly TimeSpan NumericSelectionIdleTimeout = TimeSpan.FromMilliseconds(500);

        private readonly IAnsiConsole _console;
        private readonly InteractiveScript? _script;

        public PromptUI(IAnsiConsole console, InteractiveScript? script = null)
        {
            _console = console ?? throw new ArgumentNullException(nameof(console));
            _script = script;
        }

        /// <summary>
        /// 呈现带编号的选择提示，支持脚本注入和数字键盘快捷选择。
        /// </summary>
        public T PromptSelection<T>(SelectionPrompt<T> prompt, IReadOnlyList<T> choices, Func<T, string> labelFactory, string? displayTitle)
            where T : notnull
        {
            if (_script != null && _script.TryDequeue(out T scriptedValue))
            {
                return scriptedValue;
            }

            var entries = choices
                .Select((choice, index) => new NumberedChoice<T>(index + 1, choice, labelFactory(choice)))
                .ToList();

            var lookup = entries.ToDictionary(entry => entry.Value, entry => entry, EqualityComparer<T>.Default);

            prompt.UseConverter(value =>
            {
                return lookup.TryGetValue(value, out var entry)
                    ? FormatNumberedLabel(entry)
                    : labelFactory(value);
            });

            RenderNumberedChoices(displayTitle, entries);

            if (_script is null && TrySelectByNumber(entries, out var directSelection))
            {
                return directSelection;
            }

            while (true)
            {
                var inputPrompt = new TextPrompt<string>("请输入选项序号（直接输入数字或按 Enter 使用方向键）")
                    .AllowEmpty()
                    .DefaultValue(string.Empty);

                var input = Prompt(inputPrompt);

                if (string.IsNullOrWhiteSpace(input))
                {
                    _console.WriteLine();
                    return _console.Prompt(prompt);
                }

                if (int.TryParse(input, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                    && index >= 1 && index <= entries.Count)
                {
                    _console.WriteLine();
                    return entries[index - 1].Value;
                }

                _console.MarkupLine("[red]无效的序号，请重新输入。[/]");
            }
        }

        /// <summary>
        /// 基础提示包装，支持脚本注入。
        /// </summary>
        public T Prompt<T>(IPrompt<T> prompt)
        {
            if (_script != null && _script.TryDequeue(out T scriptedValue))
            {
                return scriptedValue;
            }

            return _console.Prompt(prompt);
        }

        /// <summary>
        /// 确认对话框，支持脚本注入。
        /// </summary>
        public bool PromptConfirm(string message, bool defaultValue = false)
        {
            if (_script != null && _script.TryDequeue(out bool scriptedValue))
            {
                return scriptedValue;
            }

            return _console.Confirm(message, defaultValue);
        }

        /// <summary>
        /// 等待用户按 Enter 返回菜单。
        /// </summary>
        public void WaitForMenuReturn()
        {
            _console.WriteLine();
            var prompt = new TextPrompt<string>("[grey]按下 Enter 返回主菜单[/]")
                .AllowEmpty()
                .DefaultValue(string.Empty);
            Prompt(prompt);
        }

        private void RenderNumberedChoices<T>(string? title, List<NumberedChoice<T>> entries)
            where T : notnull
        {
            if (!string.IsNullOrWhiteSpace(title))
            {
                _console.MarkupLine(title!);
            }

            if (entries.Count > 0)
            {
                var grid = new Grid();
                grid.AddColumn(new GridColumn().NoWrap().PadLeft(0).PadRight(1));
                grid.AddColumn(new GridColumn().PadLeft(0));

                foreach (var entry in entries)
                {
                    grid.AddRow(new Markup($"[grey]{entry.Index}.[/]"), new Markup(entry.Label));
                }

                _console.Write(grid);
                _console.WriteLine();
                _console.MarkupLine("[grey]直接输入序号即可选择；按 Enter 使用方向键。[/]");
            }

            _console.WriteLine();
        }

        private static string FormatNumberedLabel<T>(NumberedChoice<T> entry)
            where T : notnull
        {
            return $"[grey]{entry.Index}.[/] {entry.Label}";
        }

        private sealed record NumberedChoice<T>(int Index, T Value, string Label)
            where T : notnull;

        private bool TrySelectByNumber<T>(List<NumberedChoice<T>> entries, out T value)
            where T : notnull
        {
            value = default!;

            if (entries.Count == 0 || Console.IsInputRedirected || _script is not null)
            {
                return false;
            }

            var buffer = new StringBuilder();
            DateTime? deadline = null;

            while (true)
            {
                if (buffer.Length > 0 && deadline.HasValue && DateTime.UtcNow >= deadline.Value)
                {
                    if (TryResolveSelection(entries, buffer.ToString(), out value))
                    {
                        return true;
                    }

                    buffer.Clear();
                    deadline = null;
                    _console.MarkupLine("[red]无效的序号，请重新输入。[/]");
                    continue;
                }

                ConsoleKeyInfo keyInfo;
                if (buffer.Length == 0)
                {
                    if (!TryReadKey(out keyInfo))
                    {
                        return false;
                    }
                }
                else
                {
                    if (!TryReadKeyIfAvailable(out keyInfo, out var pollingError))
                    {
                        if (pollingError)
                        {
                            return false;
                        }

                        Thread.Sleep(DirectNumberPollingMilliseconds);
                        continue;
                    }
                }

                if (keyInfo.Key == ConsoleKey.Enter)
                {
                    if (buffer.Length == 0)
                    {
                        _console.WriteLine();
                        return false;
                    }

                    if (TryResolveSelection(entries, buffer.ToString(), out value))
                    {
                        return true;
                    }

                    buffer.Clear();
                    deadline = null;
                    _console.MarkupLine("[red]无效的序号，请重新输入。[/]");
                    continue;
                }

                if (keyInfo.Key == ConsoleKey.Backspace)
                {
                    if (buffer.Length > 0)
                    {
                        buffer.Remove(buffer.Length - 1, 1);
                        deadline = buffer.Length == 0
                            ? null
                            : CalculateDeadline(buffer.ToString(), entries.Count);
                    }

                    continue;
                }

                if (!char.IsDigit(keyInfo.KeyChar))
                {
                    if (buffer.Length == 0)
                    {
                        continue;
                    }

                    buffer.Clear();
                    deadline = null;
                    _console.MarkupLine("[red]无效的序号，请重新输入。[/]");
                    continue;
                }

                if (buffer.Length == 0 && keyInfo.KeyChar == '0')
                {
                    _console.MarkupLine("[red]无效的序号，请重新输入。[/]");
                    continue;
                }

                buffer.Append(keyInfo.KeyChar);

                if (!int.TryParse(buffer.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                    || index < 1 || index > entries.Count)
                {
                    buffer.Clear();
                    deadline = null;
                    _console.MarkupLine("[red]无效的序号，请重新输入。[/]");
                    continue;
                }

                if (HasContinuationPotential(index, entries.Count))
                {
                    deadline = DateTime.UtcNow.Add(NumericSelectionIdleTimeout);
                    continue;
                }

                if (TryResolveSelection(entries, buffer.ToString(), out value))
                {
                    return true;
                }

                buffer.Clear();
                deadline = null;
                _console.MarkupLine("[red]无效的序号，请重新输入。[/]");
            }
        }

        private static bool TryReadKey(out ConsoleKeyInfo keyInfo)
        {
            keyInfo = default;

            try
            {
                keyInfo = Console.ReadKey(intercept: true);
                return true;
            }
            catch (InvalidOperationException)
            {
            }
            catch (IOException)
            {
            }
            catch (PlatformNotSupportedException)
            {
            }

            return false;
        }

        private static bool TryReadKeyIfAvailable(out ConsoleKeyInfo keyInfo, out bool encounteredError)
        {
            keyInfo = default;
            encounteredError = false;

            try
            {
                if (!Console.KeyAvailable)
                {
                    return false;
                }

                keyInfo = Console.ReadKey(intercept: true);
                return true;
            }
            catch (InvalidOperationException)
            {
            }
            catch (IOException)
            {
            }
            catch (PlatformNotSupportedException)
            {
            }

            encounteredError = true;
            return false;
        }

        private bool TryResolveSelection<T>(List<NumberedChoice<T>> entries, string digits, out T value)
            where T : notnull
        {
            if (int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index)
                && index >= 1 && index <= entries.Count)
            {
                _console.WriteLine();
                value = entries[index - 1].Value;
                return true;
            }

            value = default!;
            return false;
        }

        private static DateTime? CalculateDeadline(string digits, int count)
        {
            if (!int.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out var index))
            {
                return null;
            }

            return HasContinuationPotential(index, count)
                ? DateTime.UtcNow.Add(NumericSelectionIdleTimeout)
                : null;
        }

        private static bool HasContinuationPotential(int current, int count)
        {
            var candidate = (long)current * 10;
            return candidate <= count;
        }
    }
}
