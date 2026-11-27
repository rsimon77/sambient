# Sambient – terv egy LIVEN Ambient Ø-szerű virtuális szintetizátorhoz (macOS)

> Munkanév: **Sambient**. Saját név, saját presetek, saját hangminták. A „LIVEN” / „Sonicware” nevet és a gyári tartalmat nem használjuk (lásd: 9. pont).

---

## 1. Mit tud az eredeti (a manual alapján)

| Terület | Ambient Ø |
|---|---|
| Rétegek | 4 réteg: L1–L3 **BlendWave Modulation Synth**, L4 **Noise/sampler** |
| Hangforrás L1–3 | 32 WAVE × 128 HARMONIC pozíció (morfolható wavetable), 6 STRUCTURE (DRONE1/2, PAD1/2, ATMOS1/2) |
| Hangforrás L4 | 8 sztereó minta (8 s, 16 bit / 32 kHz) + fehérzaj keverés, LOOP/1SHOT, LINE IN mint forrás, mintavételezés line inről (auto-rec szint, crossfade 1–4 s, EXP/LIN görbe) |
| Rétegenként | AR burkoló (0–10 s, DRONE-nál 20 s), szűrő OFF/LPF/HPF/BPF (70–14 400 Hz, Q 0,3–10, BPF 0,5–3,3 okt), 2 LFO (24 forma, 16 cél, trigger OFF/1–8/INF), struktúra-saját MOD LFO, level, pan, →reverb, →shimmer |
| Hangszólás | POLY / MONO / LEGATO (glide 0–10 s) / UNISON (4 hang, 0–20 cent) / ARP (12 típus) |
| Polifónia | 34 oszcillátoros közös keret (DRONE 2, PAD 3, ATMOS 3, NOISE 1 osc/hang), prioritás L1 > L4 |
| Effektek | Shimmer reverb, 9 típus (Small / Large / Infinity × L/M/H), réteg-send; 6 master FX (Tape Delay, Reverse Delay, Overdrive, Bit/Rate Crush, Tilt EQ, Stereo Chorus), PRE/POST routing |
| Szekvenszer | 4 sáv, 1–64 lépés, lépéshossz 1/32 … 8/1 (triolák, pontozottak), tie, step/realtime/overwrite/direct rec, P-lock, Sound lock, valószínűség 25–100 % (DICE), RND SEQ/RND STEP, transpose, duplikálás |
| Pattern Palette | 8 bank × 16 = 128 (4 gyári bank), lánc, loop, **Relaxation mód** (N ismétlés után 5–30 s fade), pattern level, BPM 40–250, PTN/GLBL BPM |
| Hangolás | EQUA, Tiszta dúr/moll, Pythagoreus #/♭, Solfeggio (174…963 Hz), Chakra, Planetary + tonika; Master tune ±75 cent / 410–470 Hz; Tune All ±1200 cent |
| MIDI | Rétegenkénti csatorna, pattern- és auto-csatorna, CC be/ki, clock, program change |

### Mit árul el a firmware (`LIVEN_AMBIENT_1_5_45.syx`)
- Sonicware SysEx (`F0 00 48 04 …`), 8295 üzenet, 7→8 bit csomagolás, **nem titkosított**. Kicsomagolva ~1,74 MB: egy konténer (`dtmc`), benne ~620 KB preset-adat (`pmtd`: SEQN/SQPS/SQPM/SQPR/SQSD chunkok, pl. ZER0, PLNT, RAIN, DAWN…) és ~1,1 MB ARM kód.
- A C++ osztálynevek megerősítik az architektúrát, amit követni érdemes:
  - `SoundOSC::OSCWaveTableSynth` → a WAVE-ek valódi **wavetable-ök** (nem élő algoritmusok)
  - `Envelope::ARSlopeType` → tényleg csak Attack/Release
  - `Filter::FilterProperty<0/1/2>` → LP/HP/BP (valószínűleg SVF)
  - LFO alakok külön osztályokban: `Sawtooth, Triangle, Pulse, Step, RampPlus/Minus, RevLog10, RandomLFO`
  - Reverb algoritmusok: `RoomLow/Mid/Hi, HallLow/Mid/Hi, Plate, Tunnel` → a „Small/Large/Infinity × L/M/H” valószínűleg Room/Hall/(Tunnel/Plate) × sötét/közép/világos csillapítás
  - Master FX: `InsertDelay, ReverseDelay, OverDrive/DriveEffect, SamplerateCrusher, TiltFilter, Chorus`
  - `Voice::VoiceAssigner{Mono,Legato,Unison}`, `Sequencer::ArpeggiatorPlayer`, `Metronome`, `SequencerPrecount`
- A noise-minták **nincsenek** a firmware-ben (külön letölthető adat), így azokat úgyis magunknak kell elkészíteni.

---

## 2. Célplatform és technológia

**Feltétel: .NET vagy Python. Javaslat: C# / .NET (app + hangmotor), Python csak az offline eszközökhöz.**

- **Elsődleges cél: Standalone macOS app** a Launchkey MK3 49-hez (eldöntve, lásd 11. pont). AU/VST3 plugin .NET-ből és Pythonból gyakorlatilag nem készíthető – mivel a standalone a cél, ez nem veszteség.
- Csak saját gépre: arm64, macOS 13+, ad-hoc aláírás elég.

### Miért .NET és nem Python a hangmotorhoz
| Szempont | C# / .NET 11 | Python |
|---|---|---|
| Mintánkénti DSP (oszcillátor, szűrő, burkoló, reverb visszacsatolás) | JIT/NativeAOT-ra fordított kód, `Span<T>`, `Vector<T>` SIMD – C++-hoz közeli sebesség | tiszta Pythonban lehetetlen; csak `numba` JIT-tel vagy numpy blokkműveletekkel, de a visszacsatolásos részek (szűrő, reverb, burkoló) nem vektorizálhatók jól |
| Valós idejű stabilitás | nincs GIL; ha az audio callback nem allokál, a GC nem okoz kattogást | GIL: a UI és a MIDI szál Python kódja blokkolhatja az audio callbacket → kattogás; csak nagy pufferrel (1024+ minta, ~23 ms) kezelhető |
| UI macOS-en | **Avalonia** (modern, GPU-gyorsított, sötét téma, saját vezérlők) | PySide6/Qt vagy Dear PyGui – működik |
| MIDI + Launchkey SysEx | **DryWetMIDI** (macOS-en is, SysEx, port név szerinti keresés) | `python-rtmidi` / `mido` – jó |
| Audio I/O | PortAudio .NET wrapperrel (pl. PortAudioSharp) vagy CoreAudio AudioUnit P/Invoke-kal | `sounddevice` (PortAudio) – jó |

Összegezve: a 4 réteg × ~10 hang × max 3 oszcillátor + shimmer reverb + effektek C#-ban kényelmesen belefér egy magba, kis pufferrel (128–256 minta). Pythonban működőképes lenne `numba`-val és nagy pufferrel (ambient zenéhez a késleltetés még elfogadható), de sokkal több küzdelem a stabilitással.

**Ha mégis Python**: `sounddevice` + `numba` (@njit motor, minden DSP egy fordított függvényben, a Python oldal csak paramétereket ír egy megosztott numpy tömbbe) + `python-rtmidi` + PySide6. A terv többi része ugyanúgy érvényes.

### Választott stack (.NET)
- **.NET 11, C#** (a gépen ez van telepítve), `dotnet` CLI, solution több projekttel (lásd 10. pont).
- **Audio**: PortAudio wrapper (CoreAudio backend); alternatíva/tartalék: saját vékony CoreAudio AudioUnit P/Invoke réteg, ha kisebb késleltetés kell.
- **MIDI**: DryWetMIDI (Launchkey MIDI + DAW port, SysEx).
- **UI**: Avalonia 11, MVVM (CommunityToolkit.Mvvm), saját knob/fader/step-grid/hullámforma vezérlők.
- **Tesztek**: xUnit (DSP egységtesztek: frekvencia, szinten tartás, nincs NaN/denormal; szekvenszer-időzítés; preset round-trip).
- **Python (offline eszközök)**: csak a noise minták előkészítéséhez (resample 32 kHz, loop crossfade → `.wav`). A wavetable-ök C#-ban készülnek induláskor (M1 döntés: így nincs 30–170 MB bináris a gitben, és a buildhez nem kell Python).
- A library-k pontos, aktuális verzióit a projekt indításakor (M0) kell rögzíteni és kipróbálni macOS/arm64-en.

---

## 3. Architektúra

```
AudioEngine (audio callback)
 ├─ ParameterStore             ← float tömb, atomikus írás UI/MIDI szálról
 ├─ PatternBank (8×16 PatternPalette)  ← JSON/ValueTree, .sambient fájlok
 ├─ Transport/Clock            ← belső BPM, opcionális MIDI clock ki/be
 ├─ Sequencer (4 Track)        ← lépések, P-lock, tie, prob, random, arp
 ├─ VoiceAllocator             ← POLY/MONO/LGT/UNI/ARP, 34-osc „budget” opcionálisan
 ├─ Layer[0..2]: SynthLayer
 │    Voice → BlendWaveOsc ×(1..3) → (Ring/FM) → SVF → AR VCA → pan
 │    LFO1, LFO2, ModLFO
 ├─ Layer[3]: NoiseLayer
 │    Voice → SamplePlayer(stereo, loop/1shot, xfade) + WhiteNoise → SVF → AR
 ├─ Mixer: level/pan, send→Reverb, send→Shimmer, PatternLevel, FX routing PRE/POST
 ├─ ShimmerReverb (9 típus)
 └─ MasterFX (6 típus) → Master VOL
```

Szabályok:
- Az audio szálon **nincs** allokáció (C#-ban: nincs `new`, LINQ, lambda-capture, string, boxing; minden puffer és hangobjektum előre lefoglalva, `struct`-ok és `Span<T>`), nincs lock és fájl I/O. Így a GC nem állítja meg a hangot. GC beállítás: `SustainedLowLatency` mód. Pattern-váltás: előre betöltött struktúra atomikus referenciacsere.
- Szálak közti kommunikáció: lockfree egy-író/egy-olvasó gyűrűpuffer (UI/MIDI → audio: események; audio → UI: lépéspozíció, szintmérők).
- Minden „hardveres” 0–127 paraméter belül normalizált float, a kijelzett értékeket (ms, Hz, cent) a manual táblázatai szerinti leképezés adja (pl. VOL 63–64 = 0 dB, −∞…+6 dB).
- Paraméter-simítás (SmoothedValue) minden folyamatos paraméteren; WAVE/STRUCTURE váltáskor rövid crossfade.

---

## 4. DSP részletek

### 4.1 BlendWave wavetable motor (a hangzás kulcsa)
1. **Táblagenerálás induláskor, C#-ban** (`Sambient.Dsp.Wavetables`, WAVE-enként lustán, háttérszálon): 32 WAVE × 128 frame. Meghallgatáshoz: `tools/Sambient.WaveDump` (HARMONIC-söprés WAV-ba). Minden WAVE egy saját „recept” a manual leírása alapján, a HARMONIC paraméter a recept fő változója:
   - 1 SAW.S waveshaped saw · 2 SQR.D PWM · 3 SIN.S · 4 SIN.D duty-modulált szinusz · 5 SQR.S · 6 TRI.S · 7 SQ.SN · 8 EV.OD páros→páratlan felharmonikus morf · 9 OCTV sync-elt alapformák · 10 SIN.M · 11 SIN.F wavefold · 12 TRI.F · 13 SIN.C bitcrush · 14 FM.CR · 15 FM.OD · 16 SYNC · 17–18 HRM.1/2 additív + sync/FM · 19 CL.FM · 20 FRML · 21–22 FRM.1/2 formáns (FRM.2 magánhangzó-formánsokkal) · 23 27.PS (27 felharmonikus pozíció) · 24 FM.WF · 25 FM.PF 2-op FM · 26 AD.MP · 27 BCHL west-coast complex osc · 28 SQR.R zajjal AM · 29 RNDM 127 véletlen hullám · 30 S.RND morfoló véletlen · 31 TRI.N rózsaszín/fehér zaj spektrummal · 32 WH.NS fehérzaj-spektrum.
2. **Sávkorlátozás**: minden frame-ből FFT-vel oktávonként mipmap (≈10 szint), így nincs aliasing magas hangokon. A szintenként csökkenő táblahossz miatt WAVE-enként néhány MB.
3. **Lejátszás**: frame-ek közti lineáris interpoláció (HARMONIC 0–127 → frame 0–127, modulálva folyamatos), mintán belül Hermite/kubikus interpoláció, mipmap-választás a lejátszott frekvencia alapján, mipmap-szintek közti crossfade.
4. Fontos: a WH.NS-re a manual leírja, hogy HARMONIC LFO-val (63/63) **fehérzajt** ad – ezt a modulációs sebességnek audio-rátán kell követnie, tehát a HARMONIC moduláció mintánként (vagy 16-os blokkonként) frissül.

### 4.2 Struktúrák (Yin/Yang modell)
A Yang osc HARMONIC pozícióját a MOD pozitív, a Yin-ét negatív irányba tolja.

| Struktúra | Osc | MOD cél | Balance | detune |
|---|---|---|---|---|
| DRONE1 | 2 Yang | detune | osc1↔osc2 | osc2 detune; alap oktáv −2, release max 20 s |
| DRONE2 | 1 Yang + 1 Yin | HARMONIC | osc1↔osc2 | osc2 pitch+detune; oktáv −2 |
| PAD1 | 1 Yang + 2 Yin | HARMONIC | Yin↔Yang | 2 Yin pitch+detune |
| PAD2 | 2 Yang + 1 Yin | HARMONIC | osc1,2↔osc3 | osc3 pitch, Yin detune |
| ATMOS1 | 1 Yang (×3 erőforrás) | kimenet ringmoduláció | – | pitch+detune |
| ATMOS2 | 2 Yang FM pár | modulátor arány | FM mennyiség 0–127 | feedback 0–127; PITCH = arány 0,50–15,99 |

PITCH −240…+240 (valószínűleg 1/10 félhang vagy 5 cent lépés – füllel/hardverrel kalibrálni), detune −63…+63, MOD rate/depth 0–127, MOD shape 24 forma, note-on retrigger.

### 4.3 Noise réteg
- Sztereó sample player (32 kHz forrás → resample az audioeszköz rátájára), LOOP (crossfade-elt loop) / SHOT, PITCH/detune/MOD(→pitch)/depth mint L1–3-nál.
- HARMONIC = fehérzaj ↔ minta balansz (63–CNTR–63).
- LN.IN: a kiválasztott audioeszköz bemenete (Mac mikrofon vagy hangkártya line in).
- **Mintavételezés**: a bemenetről 8 s puffer, auto-start küszöb (−60…−20 dB), normalizálás, crossfade 1–4 s EXP/LIN, 8 slot. Plusz a hardveren nem létező: **WAV/AIFF import drag&drop-pal**.

### 4.4 Burkoló, szűrő, LFO
- AR: exponenciális szakaszok, 0–127 → 0–10 000 ms (log görbe), DRONE-nál 20 000 ms.
- Szűrő: TPT/ZDF State-Variable (LP/HP/BP 12 dB), cutoff 70–14 400 Hz exponenciálisan, Q 0,3–10; BP-nél sávszélesség 0,5–3,3 okt.
- LFO: 24 forma (SINE…L.S.RN; a „lower speed” változatok ugyanaz ×1/8 vagy ×1/16 rátával), rate 0–127 (≈0,01–20 Hz, kalibrálandó), trigger OFF / 1–8 ciklus / INF. Célok: TUNE, HARM, BAL, PTCH, DT.FB, LFO1 rate/depth (csak LFO2-ből), MOD rate/depth, cutoff, reso, pan, level, →shimmer, →reverb, osc level (csak LFO1-ből).

### 4.5 Hangszólás és polifónia
- VoiceAssigner: POLY, MONO (retrigger + glide), LEGATO, UNISON (4 hang, 0–20 cent szórás, sztereó szórással), ARP (UP, DOWN, U.D, D.U, U.A.D, D.A.U, RNDM, UP+1, UP+2, DN−1, DN−2, P.O; sebesség = NOTE lépéshossz × BPM; DICE valószínűség).
- Hold/latch (OK + billentyű) funkció.
- **„Classic budget” kapcsoló**: 34 oszcillátoros keret és L1>L4 prioritású lopás, ahogy a manual leírja (hiteles viselkedés); kikapcsolva korlátlan polifónia (a CPU engedi).

### 4.6 Effektek
- **Shimmer reverb**: FDN (8 vagy 16 vonal) vagy Dattorro-plate alap, 9 preset: Small (room), Large (hall), Infinity (nagyon hosszú/freeze-közeli lecsengés) × L/M/H (sötét/semleges/világos csillapítás és előszűrés). Shimmer = külön send, ami egy +12 félhang pitch shifteren (granuláris, 2 ablakos) keresztül a reverb visszacsatolási ágába megy. Reverb reset kapcsoló pattern-váltáskor (R.RST).
- **Master FX**: Tape Delay (moduláló, szaturált, szűrt feedback), Reverse Delay, Overdrive (oversamplinggal), Bit/Rate crush, Tilt EQ, Stereo Chorus. FX SPEED / FX AMOUNT mint a táblázatban. PRE/POST pattern-level routing.

### 4.7 Hangolás
- Hangolási motor: hangjegy → frekvencia függvény, temperamentum + tonika szerint. A manual táblázatai: Solfeggio (174, 285, 396, 417, 528, 639, 741, 852, 963 Hz), Chakra C4 = 256/288/320/341/384/426/480 Hz, Planetary C4 = 194,18/210,42/234,16/270/282,4/315,4/352 Hz – ezeknél a többi hang 12-TET-ben épül rá, oktáv 0-ra áll, master tune figyelmen kívül.
- Tune All ±1200 cent (20 cent lépés knobbal, 1 cent finomhangolás), pitch bend tartomány 0–24, master tune cent/Hz mód.
- Bónusz: Scala (.scl/.kbm) import, MTS-ESP támogatás.

---

## 5. Szekvenszer és Pattern Palette

- **Track** rétegenként: 1–64 lépés, lépéshossz enum (8/1 … 1/32, triolák, pontozottak), lépésenként hangok (polifon), velocity, tie-hossz, valószínűség, P-lock map (paraméter-ID → érték).
- P-lock tilos: FX SPEED/AMOUNT, tune all, reverb mix, BPM, VOL (mint a hardveren).
- Sound lock: WAVE, HARMONIC, balance, PITCH, detune, MOD, depth pillanatképe a lépésre.
- Felvételi módok: step, realtime (metronóm, előszámolás 1–8), overwrite, direct (lépés lenyomva + billentyű).
- Funkciók: step copy/paste, track copy/paste, duplicate ×2/×4, transpose (−12…+12 vagy tonika-váltás), RND SEQ / RND STEP (OFF, 1, 2, 4, 8, 16), DICE.
- **Pattern Palette** = 4 réteg hang + 4 track + globális (BPM, reverb/FX típus, temperamentum, FX routing, pattern level, hossz 1–512 ütem). 8 bank × 16.
- **Lánc + Relaxation mód**: lejátszási sor, N (2–10) ismétlés után 5–30 s fade-out, majd következő; loop opció. Ez legyen egy kiemelt „Ambient Generator” nézet (alvás/meditáció célra, időzítővel).
- Szinkron: belső óra, MIDI clock ki/be (külső eszközök vagy DAW szinkronizálására). (A SYNC/Pocket Operator audio-sync opcionális, standalone-ban hasznos.)
- Natural release stop (csak a szekvencia áll le, a hangok lecsengenek).

---

## 6. Felhasználói felület

**Eldöntve: modern szerkesztő** – a fizikai kezelést a Launchkey adja, a képernyő az áttekintésre és a mély szerkesztésre szolgál. Nincs hardverhű panel, shift/func logika és szegmenskijelző.

Képernyő-elrendezés (egy ablak, átméretezhető):
- **Felső sáv**: Pattern Palette név/bank, BPM, play/rec állapot, CPU, Launchkey kapcsolat jelző (zöld = DAW-mód aktív), aktív knob-oldal (Sound / Shift / Mixer).
- **4 réteg-oszlop** egymás mellett: wave + HARMONIC előnézet (2D hullámforma, a HARMONIC pozícióval együtt animálva), structure, PITCH/detune/balance, MOD, szűrő görbével, AR görbével, LFO1/2 mini-kijelzővel, level/pan/sendek. A kiválasztott réteg kiemelve – **ugyanaz, amit a Launchkey knobjai éppen vezérelnek**.
- **Alsó rész**: step grid 4 sávval (1–64 lépés, P-lock jelölés, valószínűség, tie), mellette a Pattern lánc / Relaxation vezérlő.
- **Lenyíló panelek**: effektek (reverb/shimmer, master FX), noise minta-szerkesztő (hullámforma, loop pontok, WAV drag&drop), hangolás, MIDI/Launchkey beállítások.
- **On-screen billentyűzet (opcionális, ki-/bekapcsolható panel)**: 2–4 oktávos, egérrel játszható (kattintás = note on, elengedés = note off, húzással glissando), oktáv ◀ ▶ váltó, velocity a billentyű függőleges kattintási pozíciójából; a számítógép billentyűzetéről is játszható (A W S E D F T G Y H U J K… sor, Z/X = oktáv). A Launchkey-ről vagy bármelyik MIDI-forrásból érkező lenyomott hangokat kiemelve mutatja. A beállítás (látszik-e) megmarad az app újraindítása után.
- Minden lista-paraméter (FLTR TYPE, LFO SHAPE, RVB TYPE …) legördülő menü – a VALUE enkóderre így nincs szükség.
- Megvalósítás: Avalonia, saját vektoros vezérlőkkel (Retina, sötét téma). A Launchkey-ről jövő változások azonnal megjelennek a képernyőn (és fordítva); a UI 30–60 Hz-en frissül, nem az audio szálról.
- MIDI learn minden paraméterre (jobb klikk → Learn), plusz a beépített Launchkey-térkép.

---

## 6/B. Novation Launchkey MK3 49 mint elsődleges vezérlő

A cél, hogy a Launchkey-jel a hardverhez hasonló élményt kapj: billentyű + knobok + 16 pad mint step-gombok, LED-visszajelzéssel.

### Mi van a Launchkey MK3 49-en, és mire használjuk
| Launchkey elem | Sambient funkció |
|---|---|
| 49 billentyű (velocity, aftertouch) | hangjáték a kiválasztott rétegen (vagy MIDI csatornánként rétegenként); aftertouch → választható cél (alapból HARMONIC vagy szűrő) |
| Pitch bend / Mod kerék | pitch bend (tartomány 0–24) / Mod → választható cél (alapból LFO1 DEPTH) |
| Sustain pedál bemenet | **HOLD** (a hardver OK + billentyű funkciója) |
| 8 knob | a hardver felső 8 rétegszintű knobja: WAVE, HARMONIC, PITCH, MOD, CUTOFF, RESO, RATE, DEPTH |
| 8 knob + Shift | másodlagos réteg: structure, balance, detune, depth, attack, release, LFO2 rate, LFO2 depth |
| 9 fader | 1–4: rétegszint (LEVEL), 5–8: réteg → REVERB send, 9 (master): VOL |
| 9 fader-gomb | 1–4: réteg némítás, 5–8: réteg → shimmer / pan oldal váltása, 9: oldalváltás |
| 16 RGB pad (2×8) | **Step mód**: a 16 lépés (LED: van hang / aktuális lépés / P-lock); **Pattern mód**: 1–16 Pattern Palette választás; **Layer mód**: rétegválasztás + némítás |
| Track ◀ ▶ | rétegváltás (1–4) |
| Pad oldal-nyilak ▲▼ | lépés-oldal (1–16 / 17–32 / …), bankváltás Pattern módban |
| Play / Stop / Record / Capture | szekvenszer indítás, natural release stop, felvétel (rec / overwrite), Capture MIDI → az utoljára játszott frázis beírása a trackbe |
| Launchkey saját Arp / Scale / Chord módok | változatlanul működnek (a billentyűzet küldi a hangokat), jól kiegészítik a Sambient saját arpját |

### Megvalósítás (eldöntve: Standalone app)
- A Sambient indításkor **automatikusan megkeresi** a Launchkey két USB MIDI portját (CoreMIDI, DryWetMIDI `InputDevice/OutputDevice`, név alapján): a **MIDI portot** (billentyűk, kerekek, pedál) és a **DAW portot** (knobok, faderek, padok, gombok, LED-ek). Nem kell kézzel beállítani semmit.
- Belépés a Launchkey DAW-módjába a Novation által publikált **Launchkey MK3 Programmer's Reference Guide** szerinti üzenetekkel; innen a padok/knobok/faderek a Sambient-hez tartoznak, és a pad színek, a step-LED-ek, a lépésjelző futófény ezen a porton állíthatók.
- Kilépéskor DAW mód kikapcsolása, hogy a Launchkey visszaálljon. Hot-plug: ki-/bedugáskor újracsatlakozás és állapot-visszaküldés (LED-ek).
- Ha közben fut egy DAW, amely szintén használja a Launchkey-t, a Sambient figyelmeztet (DAW port foglalt / ütközés).
- Audio: a standalone ablakban választható kimeneti eszköz, pufferméret (alapból 256), és bemenet a LINE IN / mintavételezés funkcióhoz (pl. a Mac mikrofonja vagy egy hangkártya bemenete).

### Kódstruktúra-kiegészítés
```
src/Sambient.Control/
 ├─ IControllerSurface.cs      # absztrakt: knob/fader/pad/gomb események + LED kimenet
 ├─ LaunchkeyMk3Surface.cs     # portkeresés név alapján, DAW-mód be/ki, pad LED-ek
 ├─ ControlMapper.cs           # oldalak (Sound / Shift / Mixer), Step/Pattern/Layer pad módok
 └─ MidiLearn.cs               # általános CC-hozzárendelés, mentés JSON-ba
```
- A LED-frissítés nem az audio szálon fut: a szekvenszer lockfree FIFO-ba írja az aktuális lépést, a UI/vezérlő szál 30–60 Hz-en küldi ki a változásokat.
- Mivel a Launchkey knobjai **abszolút** potméterek (nem végtelen enkóderek), a hardver **LATCH** módját (a knob csak akkor kezd hatni, ha eléri a tárolt értéket) rétegváltáskor és pattern-betöltéskor implementálni kell, különben ugrálnak az értékek. Választható: Jump / Latch (pickup) / Scale.
- A VALUE enkóder szerepét (lista-paraméterek: FLTR TYPE, LFO SHAPE, RVB TYPE, FX TYPE, NOTE stb.) a képernyőn egérrel, a Launchkey-en pedig egy kijelölt knob „lista módban” látja el.

### Tesztelés
- Mérföldkő: **M8.5 – Launchkey integráció** (1–2 hét): portfelismerés, DAW-mód handshake, knob/fader/pad mapping, step-LED futófény, latch, hot-plug (ki-/bedugás menet közben).
- A pontos üzeneteket (DAW mód, pad-színek, a 49-es fader CC-k) a Novation hivatalos programozói dokumentációjából kell venni, és egy kis `tools/launchkey-probe` parancssori eszközzel ellenőrizni a saját eszközödön.

---

## 7. Hangok (presetek és minták)

- **64 saját gyári Pattern Palette** 4 bankban (pl. Drone, Pad, Atmos/Textúra, Generatív szekvenciák), plusz 4 üres user bank.
- **8 saját noise minta** a kategóriák szerint: eső, tűz, morajlás (rumble), erdő, tenger, szél, fák/rovarok, fuvola/légies textúra. Forrás: saját terepfelvétel vagy CC0 licencű felvételek (pl. Freesound CC0 szűrővel), 32 kHz-re, 8 s-ra vágva, loop-crossfade-del.
- **Hangzási kalibráció (hardver nélkül, eldöntve)**: nincs A/B összevetés, ezért a cél **hasonló karakter**, nem bitre azonos hang. Támpontok:
  - a manual számszerű tartományai (ms, Hz, cent, Q) – ezek pontosan átvehetők;
  - a WAVE-leírások mint hangtervezési receptek;
  - nyilvános bemutató videók és hangpéldák füllel, referenciaként (nem mintavételezve);
  - egy belső „WAVE-böngésző” a szerkesztőben: HARMONIC végigsöprés minden WAVE-en, hogy a 32 recept egymástól jól elkülönülő, zenei legyen.
- A görbék (0–127 → fizikai érték) mind egy központi `ParamCurves` táblában vannak, így később füllel könnyen finomhangolhatók.

---

## 8. Ütemterv (mérföldkövek)

| # | Mérföldkő | Tartalom | Becsült idő (1 fő, hobbi tempó) |
|---|---|---|---|
| M0 | Váz | .NET solution, Avalonia ablak, PortAudio kimenet (szinuszteszt, kattogásmentes 256-os pufferrel), DryWetMIDI: Launchkey portok listázása | 2–3 nap |
| M1 | Egy réteg szól | C# wavetable-generátor + 6 WAVE, mipmap osc, HARMONIC morf, AR, SVF, POLY; **Launchkey billentyű + 8 knob alap CC-vel** (már itt játszható) | 1–2 hét |
| M2 | BlendWave teljes | 6 struktúra, Yin/Yang, MOD LFO, ATMOS1 ring, ATMOS2 FM+feedback, mind a 32 WAVE recept | 2–3 hét |
| M3 | 4 réteg + noise | NoiseLayer, minták, loop xfade, line-in, mintavételezés, mixer | 1–2 hét |
| M4 | Moduláció & hangszólás | 2 LFO × 24 forma × 16 cél, triggerek, MONO/LGT/UNI/ARP, hold, budget mód | 1–2 hét |
| M5 | Effektek | Shimmer reverb 9 típus, 6 master FX, routing | 2–3 hét |
| M6 | Szekvenszer | trackek, felvételi módok, tie, P-lock, sound lock, prob, random, arp-sync, MIDI clock | 3–4 hét |
| M7 | Pattern Palette | bankok, mentés/betöltés, lánc, Relaxation mód, hangolási rendszerek | 1–2 hét |
| M6.5 | Launchkey MK3 teljes | DAW-mód, faderek, padok mint step-gombok, LED futófény, pad módok, latch/pickup, hot-plug | 1–2 hét |
| M8 | UI | modern szerkesztő: réteg-oszlopok, step grid, lenyíló panelek, MIDI learn | 2–3 hét |
| M9 | Hangtervezés | 64 preset, 8 minta, füllel hangolás | 2–3 hét |
| M10 | Befejezés | teljesítmény-optimalizálás (`Vector<T>` SIMD, voice-csökkentés, esetleg NativeAOT), `dotnet publish` → ad-hoc aláírt .app az Applications mappába | 2–3 nap |

A Launchkey-integráció azért kerül előre (M1-ben az alap, M6.5-ben a teljes), mert ez a fő kezelőfelület: a szekvenszer kész állapotában rögtön a padokon tesztelhető.

Összesen kb. **4–5 hónap** hobbi tempóban; egy játszható MVP kb. 5–7 hét alatt megvan.

**MVP definíció**: Standalone app, 3 szintiréteg + noise réteg, 6 struktúra, legalább 16 WAVE, AR + szűrő + 1 LFO, shimmer reverb (1–2 típus), POLY/UNI, egyszerű generikus UI, Launchkey billentyűk + 8 knob + faderek működnek.

---

## 9. Jogi és etikai keretek

- **Clean-room megközelítés**: a működést a nyilvános manual és saját fül/mérés alapján építjük újra. A firmware-ből **nem** másolunk kódot, wavetable-t, preset-adatot vagy egyéb assetet; a fenti firmware-elemzés csak a szerkezet megértésére szolgált.
- Saját név, saját grafika, saját presetek és minták. A „LIVEN”, „Ambient Ø”, „Sonicware” nevek nem szerepelnek a termékben (legfeljebb leíró összehasonlításként a dokumentációban).
- Opcionális, csak személyes használatra: import a **saját** hardveren készült Pattern Palette exportokból (PT.EX SysEx) – ez interoperabilitási funkció, a formátum dokumentálása saját exportok összevetésével történjen.

---

## 10. Repó-struktúra (javaslat)

```
Sambient/
 ├─ Sambient.sln
 ├─ tools/                 # Python (numpy/scipy), offline
 │   └─ noiseprep/         # minták: 32 kHz, 8 s, loop crossfade → .wav
 ├─ resources/wavetables/  resources/noise/  resources/presets/
 ├─ src/
 │   ├─ Sambient.Dsp/      # BlendWaveOsc, Structures, Svf, ArEnv, Lfo, SamplePlayer, Reverb, Shimmer, Fx/*
 │   ├─ Sambient.Engine/   # SynthLayer, NoiseLayer, VoiceAllocator, Arp, Tuning, Mixer, AudioEngine, ParameterStore
 │   ├─ Sambient.Seq/      # Track, Step, Sequencer, PatternPalette, Bank, Chain, Relaxation
 │   ├─ Sambient.Control/  # Launchkey MK3, ControlMapper, MidiLearn
 │   ├─ Sambient.Audio/    # PortAudio/CoreAudio I/O réteg
 │   └─ Sambient.App/      # Avalonia UI (Views, ViewModels, saját vezérlők)
 └─ tests/Sambient.Tests/  # xUnit: DSP, szekvenszer-időzítés, preset round-trip
```

## 11. Döntések

| Kérdés | Döntés | Következmény |
|---|---|---|
| Nyelv | **.NET (C#)** az apphoz, Python az offline eszközökhöz | Avalonia UI, DryWetMIDI, PortAudio; plugin formátum nincs |
| Fő használati mód | **Standalone app** a Launchkey MK3 49-cel | teljes DAW-módú integráció, LED-ek |
| Képernyős UI | **Modern szerkesztő** | nincs hardverhű panel; a Launchkey a fizikai kezelőfelület |
| Hardver a kalibrációhoz | **Nincs** | hasonló karakter a cél, füllel hangolva, a manual számszerű adataira építve |
| Terjesztés | **Csak saját használat** | nincs notarizáció/installer; a használt library-k (MIT/BSD-szerű) licence nem korlátoz |

Még nyitott (ráér később):
- iPad/AUv3 kell-e valaha? (Most nem tervezzük.)
- A billentyűzet egy rétegre szóljon (a kiválasztottra), vagy osztott billentyűzet (pl. alsó oktávok = DRONE réteg, felső = PAD)? A Launchkey 49 billentyűje erre alkalmas lenne.
