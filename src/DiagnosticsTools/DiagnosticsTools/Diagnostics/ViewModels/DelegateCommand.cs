using System;
using System.Threading.Tasks;
using System.Windows.Input;

namespace Avalonia.Diagnostics.ViewModels
{
    internal sealed class DelegateCommand : ICommand
    {
        private readonly Func<object?, Task> _executeAsync;
        private readonly Func<object?, bool>? _canExecute;

        public DelegateCommand(Action execute, Func<bool>? canExecute = null)
            : this(_ => { execute(); return Task.CompletedTask; },
                canExecute is null ? null : new Func<object?, bool>(_ => canExecute()))
        {
        }

        public DelegateCommand(Func<Task> execute, Func<bool>? canExecute = null)
            : this(_ => execute(), canExecute is null ? null : new Func<object?, bool>(_ => canExecute()))
        {
        }

        public DelegateCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
            : this(parameter =>
            {
                execute(parameter);
                return Task.CompletedTask;
            }, canExecute)
        {
        }

        public DelegateCommand(Func<object?, Task> executeAsync, Func<object?, bool>? canExecute = null)
        {
            _executeAsync = executeAsync ?? throw new ArgumentNullException(nameof(executeAsync));
            _canExecute = canExecute;
        }

        public event EventHandler? CanExecuteChanged;

        public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;

        public async void Execute(object? parameter)
        {
            await _executeAsync(parameter).ConfigureAwait(false);
        }

        public void RaiseCanExecuteChanged() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
    }
}
