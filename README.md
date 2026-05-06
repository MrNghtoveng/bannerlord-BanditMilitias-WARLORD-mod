HYBRID AI AND DYNAMIC WORLD SIMULATION FOR MOUNT & BLADE II: BANNERLORD 
BANNERLORD İÇİN HİBRİT YAPAY ZEKA VE DİNAMİK DÜNYA SİMÜLASYONU

BANDİT MILITAS: WARLORD EDITION, Bannerlord'daki klasik haydut partilerini daha otonom, stratejik ve bölgesel etkileri güçlü aktörlere dönüştürmeyi hedefleyen deneysel bir mod projesidir. Mod; geliştirilmeye, ek modüller ve yeni özellikler eklenmeye açık bir mimariyle tasarlanmaya çalışılmıştır;  henüz tam kapasiteyle test edilememiştir.

BANDIT MILITIAS: WARLORD EDITION is an experimental mod project that aims to transform standard bandit parties in Bannerlord into more autonomous and strategic actors with significant regional influence. The mod’s architecture was intended to be designed for easy expansion, allowing for additional modules and new features; however, it has not yet been tested to its full capacity.

[!WARNING]
TR: Bu proje aktif geliştirme aşamasındadır ve henüz test süreci devam etmektedir. Hatalar, dengesiz oynanış, kayıt uyumsuzluğu, yapay zeka davranışsal bozuklukları, Harmony hataları, diğer modlarla uyumsuzluklar ve oyun içi çökmeler beklenebilir.

EN: This project is under active development and is still in the testing phase. Expect bugs, unstable balance, save incompatibilities, behavioral AI issues, Harmony errors, mod conflicts, and potential in-game crashes.

GENEL BAKIŞ / OVERVIEW

TR: Bu mod, TaleWorlds motorunun statik yapay zeka sınırlarını asenkron veri işleme ve sistem tabanlı karar yapılarıyla zorlamayı amaçlar. Modüler bir yapıda kurgulanan proje, yeni özelliklerin eklenmesine imkan tanıyacak şekilde tasarlanmaya çalışılsa da şu an için yoğun bir test ve optimizasyon sürecindedir.


EN: This mod pushes the engine's static AI limits through asynchronous data processing and system-driven decision logic. While the modular architecture aims to support future expansions, it is currently undergoing rigorous testing and optimization.

OYNANIŞ DİNAMİKLERİ / GAMEPLAY DYNAMICS
BÖLGESEL HAKİMİYET / REGIONAL DOMINANCE

TR: Haydutlar köyleri fiziksel olarak ablukaya alabilir. Gelecek aşamalarda bu özelliğin kale ve şehirleri de kapsayacak şekilde genişletilmesi planlanmaktadır. "Haydut Bölgesi" ilan edilen yerlerde üretim durur ve krallık otoritesi geriler.
EN: Bandits can physically blockade villages. Expansion to include castles and cities is a planned future feature. In "Bandit Zones," production halts and kingdom authority deteriorates.

EKONOMİK YIKIM (KELEBEK ETKİSİ) / ECONOMIC DISRUPTION

TR: Kritik ticaret düğümlerinin ele geçirilmesi, şehirlere hammadde akışını keserek kıtlık ve isyanları tetikleyebilir.
EN: Seizing critical trade nodes can sever raw material supply to cities, potentially triggering shortages and rebellions.

EVRİM SINIFLARI / EVOLUTION CLASSES
TR: Warlord kariyer sistemi, grupların güç kazandıkça geçtiği 6 aşamalı bir yükselme yapısı sunar:
EN: The Warlord career system utilizes a six-stage progression structure as groups gain power:

Eşkıya / Outlaw: Hayatta kalma mücadelesi veren ve küçük ölçekli yağmalarla örgütlenmeye başlayan başlangıç aşamasıdır. / The initial stage focused on survival and small-scale raiding as organization begins. 

Rebel: Yerel düzeni bozmaya başlayan ve otoritelerin dikkatini çeken isyancı çekirdektir. / An insurgent phase that begins to actively destabilize local order and attract authority. 

Famous Bandit: Bölgesel olarak tanınan, daha fazla adam ve kaynak toplayabilen haydut lideridir. / A regionally recognized leader with expanded reach, manpower, and resources. 

Warlord: Artık sadece bir çete lideri değil, askeri ağırlığı olan gerçek bir güç odağıdır. / No longer a mere gang leader, but a genuine military focal point. 

Tanınmış / Distinguished: Diplomatik etkisi büyüyen ve çevre krallıklar tarafından ciddiye alınan bir yapıdır. / A power bloc taken seriously by surrounding factions with growing strategic weight. 

Fatih / Conqueror: Hegemonya kurmaya yaklaşan, bölgesel hakimiyetin zirvesindeki en üst kariyer seviyesidir. / The pinnacle of the career path, representing total regional hegemony. 

MİMARİ ÖZETİ / ARCHITECTURE SNAPSHOT
Katman / Layer	Bileşenler / Components	Görev / Function
Stratejik / Strategic	BanditBrain, WarlordSystem, HTNEngine	
Küresel analiz ve hedef belirleme / Global analysis and intent. 

Operasyonel / Operational	MilitiaDecider, AIScheduler, SwarmCoordinator	
Kararları eyleme dökme ve zamanlama / Execution and scheduling. 

Fiziksel / Performance	SpatialGridSystem, PartyCleanup, FearSystem	
Uzamsal sorgular ve sistem kararlılığı / Spatial queries and stability. 

🛠 KURULUM / INSTALLATION
Yükleme Sırası / Load Order:

Bannerlord.Harmony

Bannerlord.UIExtenderEx

Bannerlord.ButterLib

MCMv5 (Opsiyonel / Optional)

BanditMilitias

📜 ATIF VE TEŞEKKÜR / ATTRIBUTION

TR: Bu proje, *JungleDruid* tarafından geliştirilen orijinal BanditMilitias modunu temel alan bir yeniden yorumlama çalışmasıdır. WARLORD Edition, bu temel üzerine daha geniş yapay zeka, koordinasyon ve dünya simülasyonu hedefleri ekler.


EN: This project is a reinterpretation built on top of the original BanditMilitias mod by *JungleDruid*. The WARLORD Edition expands that foundation with broader AI coordination and world simulation objectives.

Project Lead: MrNghtoveng


AI-Assisted Development Tools: Antigravity IDE, Codex, Claude 
