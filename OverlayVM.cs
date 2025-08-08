using Playnite.SDK;
using Playnite.SDK.Models;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace OverlaySearch
{
    public class DisplayGame
    {
        public Game Core { get; }
        public string Name => Core.Name;
        public string Cover { get; }
        public DisplayGame(Game g, string coverPath) { Core = g; Cover = coverPath; }
    }

    public class OverlayVM : INotifyPropertyChanged
    {
        private readonly IPlayniteAPI api;
        private readonly List<Game> all;

        public ObservableCollection<DisplayGame> Filtered { get; } =
            new ObservableCollection<DisplayGame>();

        private string query = "";
        public string Query
        {
            get => query;
            set { if (query != value) { query = value; ApplyFilter(); OnPropertyChanged(); } }
        }

        private Game selectedGame;
        public Game SelectedGame
        {
            get => selectedGame;
            set { if (selectedGame != value) { selectedGame = value; OnPropertyChanged(); } }
        }

        public OverlayVM(IPlayniteAPI api)
        {
            this.api = api;
            all = api.Database.Games.ToList();
            ApplyFilter();
        }

        private void ApplyFilter()
        {
            var kw = (query ?? "").ToLowerInvariant();
            Filtered.Clear();

            foreach (var g in all)
            {
                var hay = (g.Name + " " + (g.Notes ?? "")).ToLowerInvariant();
                if (!hay.Contains(kw)) continue;

                string cover = null;
                if (!string.IsNullOrEmpty(g.CoverImage))
                    cover = api.Database.GetFullFilePath(g.CoverImage);

                Filtered.Add(new DisplayGame(g, cover));
            }

            if (Filtered.Count > 0)
                SelectedGame = Filtered[0].Core;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        private void OnPropertyChanged([CallerMemberName] string name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}