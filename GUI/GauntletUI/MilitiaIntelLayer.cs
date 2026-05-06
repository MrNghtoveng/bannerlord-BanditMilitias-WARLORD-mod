using BanditMilitias.GUI.ViewModels;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.Library;
using TaleWorlds.ScreenSystem;

namespace BanditMilitias.GUI.GauntletUI
{
    public class MilitiaIntelLayer
    {
        private static MilitiaIntelLayer? _activeInstance;
        private GauntletLayer? _layer;
        private LackeyVM? _vm;
        private GauntletMovieIdentifier? _movie;
        private bool _isMovieLoaded = false;

        public void Open(MobileParty party)
        {
            if (party == null) return;
            if (_layer != null) return;
            if (ScreenManager.TopScreen == null) return;

            if (_activeInstance != null && !ReferenceEquals(_activeInstance, this))
            {
                _activeInstance.Close();
            }

            _layer = new GauntletLayer("LackeyPanelLayer", 1000);
            _vm = new LackeyVM(party, Close);
            _movie = _layer.LoadMovie("LackeyPanel", _vm);
            _isMovieLoaded = true;

            _layer.InputRestrictions.SetInputRestrictions(true, InputUsageMask.All);
            ScreenManager.TopScreen.AddLayer(_layer);
            _layer.IsFocusLayer = true;
            ScreenManager.TrySetFocus(_layer);
            _activeInstance = this;
        }

        public void Close()
        {
            if (_layer == null) return;

            if (_isMovieLoaded && _movie != null)
            {
                _layer.ReleaseMovie(_movie);
                _isMovieLoaded = false;
            }

            ScreenManager.TopScreen.RemoveLayer(_layer);
            _layer.IsFocusLayer = false;
            _layer = null;
            _vm = null;
            _movie = null;

            if (ReferenceEquals(_activeInstance, this))
            {
                _activeInstance = null;
            }
        }
    }
}
