#if !MCM_PRESENT
using System;

namespace MCM.Abstractions.Base.Global
{

    public abstract class AttributeGlobalSettings<T>
        where T : AttributeGlobalSettings<T>, new()
    {
        private static readonly Lazy<T> _instance = new Lazy<T>(() => new T());

        public static T Instance => _instance.Value;

        public virtual string Id => typeof(T).Name;
        public virtual string DisplayName => typeof(T).Name;
        public virtual string FolderName => "BanditMilitias";
        public virtual string FormatType => "json";
    }
}

namespace MCM.Abstractions.Attributes
{
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class SettingPropertyGroupAttribute : Attribute
    {
        public SettingPropertyGroupAttribute(string groupName)
        {
            GroupName = groupName;
        }

        public string GroupName { get; }
        public int GroupOrder { get; set; }
    }
}

namespace MCM.Abstractions.Attributes.v2
{
    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class SettingPropertyBoolAttribute : Attribute
    {
        public SettingPropertyBoolAttribute(string displayName)
        {
            DisplayName = displayName;
        }

        public string DisplayName { get; }
        public int Order { get; set; }
        public bool RequireRestart { get; set; }
        public string HintText { get; set; } = string.Empty;
    }

    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class SettingPropertyIntegerAttribute : Attribute
    {
        public SettingPropertyIntegerAttribute(string displayName, int minValue, int maxValue, string format)
        {
            DisplayName = displayName;
            MinValue = minValue;
            MaxValue = maxValue;
            Format = format;
        }

        public string DisplayName { get; }
        public int MinValue { get; }
        public int MaxValue { get; }
        public string Format { get; }
        public int Order { get; set; }
        public bool RequireRestart { get; set; }
        public string HintText { get; set; } = string.Empty;
    }

    [AttributeUsage(AttributeTargets.Property, AllowMultiple = false, Inherited = true)]
    public sealed class SettingPropertyFloatingIntegerAttribute : Attribute
    {
        public SettingPropertyFloatingIntegerAttribute(string displayName, float minValue, float maxValue, string format)
        {
            DisplayName = displayName;
            MinValue = minValue;
            MaxValue = maxValue;
            Format = format;
        }

        public string DisplayName { get; }
        public float MinValue { get; }
        public float MaxValue { get; }
        public string Format { get; }
        public int Order { get; set; }
        public bool RequireRestart { get; set; }
        public string HintText { get; set; } = string.Empty;
    }
}
#endif