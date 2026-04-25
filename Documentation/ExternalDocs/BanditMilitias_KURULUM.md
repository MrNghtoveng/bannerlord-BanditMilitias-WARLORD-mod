# Kurulum Rehberi

## 1. Zorunlu Modlar

Asagidaki uc mod kurulu olmadan BanditMilitias acilmaz:

1. `Bannerlord.Harmony`
2. `Bannerlord.UIExtenderEx`
3. `Bannerlord.ButterLib`

## 2. Opsiyonel Mod

1. `MCMv5` (opsiyonel)

MCM kurulu degilse mod varsayilan ayarlarla calisir.

## 3. Mod Yukleme Sirasi

1. `Bannerlord.Harmony`
2. `Bannerlord.UIExtenderEx`
3. `Bannerlord.ButterLib`
4. `MCMv5` (varsa)
5. `BanditMilitias`

## 4. Derleme Secenekleri

Varsayilan (MCM'siz):

```powershell
dotnet build BanditMilitias.csproj -c Release
```

MCM ile:

```powershell
dotnet build BanditMilitias.csproj -c Release /p:UseMcm=true
```

## 5. Kontrol Listesi

1. `SubModule.xml` icinde zorunlu bagimliliklar yuklu mu?
2. Oyun load order'da dependency sirasi dogru mu?
3. MCM kullanacaksan `MCMv5` modulu etkin mi?
