using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Controls;

namespace Caelum.Models
{
    public class AppTab : INotifyPropertyChanged
    {
        private string _title = "Home";
        private string _icon = "\uE80F";
        private string _filePath;
        private Frame _frame;
        private bool _isActive;

        public string Id { get; } = Guid.NewGuid().ToString("N");

        public string Title
        {
            get => _title;
            set { _title = value; OnPropertyChanged(); }
        }

        public string Icon
        {
            get => _icon;
            set { _icon = value; OnPropertyChanged(); }
        }

        /// <summary>
        /// null for Home tabs, file path for editor tabs.
        /// </summary>
        public string FilePath
        {
            get => _filePath;
            set { _filePath = value; OnPropertyChanged(); }
        }

        public bool IsHome => string.IsNullOrEmpty(_filePath);

        public Frame Frame
        {
            get => _frame;
            set { _frame = value; OnPropertyChanged(); }
        }

        public bool IsActive
        {
            get => _isActive;
            set { _isActive = value; OnPropertyChanged(); }
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
