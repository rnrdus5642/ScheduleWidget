using System;
using System.Windows.Input;
using System.Windows.Threading;

namespace ScheduleWidget
{
    // One instance per editor, shared by its title/time fields.
    internal sealed class EnterKeySubmit
    {
        private bool pending;

        internal void Handle(KeyEventArgs args, Action submit)
        {
            bool ime = args.Key == Key.ImeProcessed;
            Key key = ime ? args.ImeProcessedKey : args.Key;
            if (key != Key.Enter || Keyboard.Modifiers != ModifierKeys.None) return;
            if (!ime) args.Handled = true;
            if (args.IsRepeat || pending) return;
            if (!ime) { submit(); return; }

            // Let the IME finish the final syllable before reading Text. Consuming this preview event would
            // prevent that commit; a bubbling KeyDown alone can miss it because the IME handles the key.
            pending = true;
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
            {
                pending = false;
                submit();
            }));
        }
    }
}
