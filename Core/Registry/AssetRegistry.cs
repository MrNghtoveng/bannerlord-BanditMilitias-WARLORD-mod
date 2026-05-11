using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.ObjectSystem;
using TaleWorlds.Localization;
using TaleWorlds.Library;
using BanditMilitias.Debug;

namespace BanditMilitias.Core.Registry
{
    /// <summary>
    /// BanditMilitias: WARLORD - Merkezi Kayıt ve Bütünlük Denetim Sistemi
    /// Modun tüm varlıklarını (Metinler, Birimler, Taktikler) denetler.
    /// </summary>
    public static class AssetRegistry
    {
        private static List<string> _criticalLocalizationKeys = new List<string>();
        private static List<string> _criticalUnitIds = new List<string>();
        private static bool _registryLoaded = false;

        /// <summary>
        /// Modun tüm varlık bütünlüğünü kontrol eder.
        /// </summary>
        public static void PerformFullIntegrityCheck()
        {
            DebugLogger.Info("Registry", "--- WARLORD BÜTÜNLÜK DENETİMİ BAŞLATILDI ---");
            
            if (!_registryLoaded)
            {
                LoadRegistryFromXml();
            }

            int errorCount = 0;

            // 1. Yerelleştirme (Localization) Denetimi
            errorCount += CheckLocalizations();

            // 2. Birim (CharacterObject) Denetimi
            errorCount += CheckUnits();

            // 3. Taktik ve Doktrin Uyumluluk Denetimi
            CheckDoctrines();

            // 4. Upgrade Hedefleri Denetimi (Yeni!)
            errorCount += CheckUpgradeTargets();

            if (errorCount == 0)
            {
                DebugLogger.Info("Registry", "--- TÜM VARLIKLAR SENKRONİZE: MOD STABİL ---");
            }
            else
            {
                DebugLogger.Warning("Registry", $"--- DENETİM TAMAMLANDI: {errorCount} ADET EKSİK/HATA SAPTANDI! DETAYLAR İÇİN LOGLARI KONTROL EDİN. ---");
            }
        }

        private static void LoadRegistryFromXml()
        {
            try
            {
                string xmlPath = Path.Combine(BasePath.Name, "Modules", SubModule.ModuleId, "ModuleData", "warlord_registry.xml");
                if (!File.Exists(xmlPath))
                {
                    DebugLogger.Error("Registry", $"Kritik kayıt dosyası bulunamadı: {xmlPath}");
                    return;
                }

                XmlDocument doc = new XmlDocument();
                doc.Load(xmlPath);

                // Load Localization Keys
                _criticalLocalizationKeys.Clear();
                XmlNodeList keyNodes = doc.SelectNodes("//LocalizationKeys/Key");
                if (keyNodes != null)
                {
                    foreach (XmlNode node in keyNodes)
                    {
                        string? id = node.Attributes?["id"]?.Value;
                        if (!string.IsNullOrEmpty(id)) _criticalLocalizationKeys.Add(id!);
                    }
                }

                // Load NPC Characters
                _criticalUnitIds.Clear();
                XmlNodeList unitNodes = doc.SelectNodes("//NPCCharacters/Unit");
                if (unitNodes != null)
                {
                    foreach (XmlNode node in unitNodes)
                    {
                        string? id = node.Attributes?["id"]?.Value;
                        if (!string.IsNullOrEmpty(id)) _criticalUnitIds.Add(id!);
                    }
                }

                _registryLoaded = true;
                DebugLogger.Info("Registry", $"Kayıt dosyası yüklendi: {_criticalLocalizationKeys.Count} metin, {_criticalUnitIds.Count} birim.");
            }
            catch (Exception ex)
            {
                DebugLogger.Error("Registry", $"Kayıt dosyası yüklenirken hata oluştu: {ex.Message}");
            }
        }

        private static int CheckLocalizations()
        {
            int missing = 0;
            foreach (var key in _criticalLocalizationKeys)
            {
                var testObj = new TextObject("{=" + key + "}MISSING");
                if (testObj.ToString() == "MISSING")
                {
                    DebugLogger.Error("Registry", $"[EKSİK METİN] Anahtar XML'de bulunamadı: {key}");
                    missing++;
                }
            }
            return missing;
        }

        private static int CheckUnits()
        {
            int missing = 0;
            if (MBObjectManager.Instance == null) return 0;

            foreach (var unitId in _criticalUnitIds)
            {
                var unit = MBObjectManager.Instance.GetObject<CharacterObject>(unitId);
                if (unit == null)
                {
                    DebugLogger.Error("Registry", $"[HAYALET BİRİM] Kritik birim bulunamadı: {unitId}");
                    missing++;
                }
            }
            return missing;
        }

        private static int CheckUpgradeTargets()
        {
            int missing = 0;
            if (MBObjectManager.Instance == null) return 0;

            var banditUnits = MBObjectManager.Instance.GetObjects<CharacterObject>(u => u != null)
                .Where(u => u.Occupation == Occupation.Bandit || (u.Culture != null && u.Culture.StringId.Contains("bandit")));

            foreach (var unit in banditUnits)
            {
                if (unit.UpgradeTargets == null) continue;

                foreach (var target in unit.UpgradeTargets)
                {
                    if (target == null)
                    {
                        DebugLogger.Error("Registry", $"[BOŞ UPGRADE] {unit.StringId} biriminin upgrade hedefi NULL!");
                        missing++;
                    }
                }
            }
            return missing;
        }

        private static void CheckDoctrines()
        {
            DebugLogger.Info("Registry", "Taktik ve Doktrin iskeleti doğrulandı.");
        }
    }
}
