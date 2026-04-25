using System;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;

namespace BanditMilitias.GUI.ViewModels
{
    public class LackeyVM : ViewModel
    {
        private readonly MobileParty _targetParty;
        private string _titleText = string.Empty;
        private string _leaderName = string.Empty;
        private string _powerText = string.Empty;
        private string _troopCountText = string.Empty;
        private Action _onClose;

        public LackeyVM(MobileParty party, Action onClose)
        {
            _targetParty = party;
            _onClose = onClose;

            RefreshValues();
        }

        public override void RefreshValues()
        {
            base.RefreshValues();
            TitleText = "Bandit Militia Intel";

            if (_targetParty != null)
            {
                LeaderName = _targetParty.LeaderHero != null ? _targetParty.LeaderHero.Name.ToString() : _targetParty.Name.ToString();

                float power = Infrastructure.CompatibilityLayer.GetTotalStrength(_targetParty);
                PowerText = $"Estimated Power: {power:F0}";

                TroopCountText = $"Troops: {_targetParty.MemberRoster.TotalManCount} (Wounded: {_targetParty.MemberRoster.TotalWounded})";
            }
            else
            {
                LeaderName = "Unknown";
                PowerText = "N/A";
                TroopCountText = "N/A";
            }
        }

        [DataSourceProperty]
        public string TitleText
        {
            get => _titleText;
            set
            {
                if (value != _titleText)
                {
                    _titleText = value;
                    OnPropertyChangedWithValue(value, "TitleText");
                }
            }
        }

        [DataSourceProperty]
        public string LeaderName
        {
            get => _leaderName;
            set
            {
                if (value != _leaderName)
                {
                    _leaderName = value;
                    OnPropertyChangedWithValue(value, "LeaderName");
                }
            }
        }

        [DataSourceProperty]
        public string PowerText
        {
            get => _powerText;
            set
            {
                if (value != _powerText)
                {
                    _powerText = value;
                    OnPropertyChangedWithValue(value, "PowerText");
                }
            }
        }

        [DataSourceProperty]
        public string TroopCountText
        {
            get => _troopCountText;
            set
            {
                if (value != _troopCountText)
                {
                    _troopCountText = value;
                    OnPropertyChangedWithValue(value, "TroopCountText");
                }
            }
        }

        public void ExecuteClose()
        {
            _onClose?.Invoke();
        }
    }
}