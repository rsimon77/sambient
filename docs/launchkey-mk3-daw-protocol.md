# Novation Launchkey MK3 (25/37/49/61/88) - DAW-mode MIDI protocol spec

Primary source (OFFICIAL, read in full, 18 pages, "Programmer's Reference Guide Version 1"):
`https://fael-downloads-prod.focusrite.com/customer/prod/downloads/launchkey_mk3_programmer_s_reference_guide_v1_en.pdf`
(Focusrite hosts Novation's downloads; linked from downloads.focusrite.com/novation/launchkey-mk3/launchkey-49-mk3). Cited below as **[PRG p.N]**.
Secondary: Launchkey MK3 User Guide v6 EN (same host, `.../downloads/Launchkey%20MK3%20User%20Guide%20v6%20-%20EN.pdf`) cited **[UG]**; tenshells/LaunchKeyMK3 FL Studio script (https://github.com/tenshells/LaunchKeyMK3, file `device_Novation Launchkey 49 MK3.py`) cited **[FL]**.

Notation: `Ch n` = MIDI channel 1-16 (status nibble n-1). Status bytes: Ch1 `90/B0`, Ch16 `9F/BF`. Middle C = note 60. All numbers decimal unless `h`/`0x`.
Many tables in the PDF are IMAGES (button map, pad indices, CC indices, palette); I rendered them and transcribed them. The PDF figures do NOT label buttons by name; names below come from the figure positions + [UG] + [FL] and are flagged where inferred.

---
## 1. USB MIDI ports

[PRG p.4] Two USB MIDI interfaces (each one in + one out) plus a DIN MIDI OUT:
- **MIDI port** ("Launchkey MK3 MIDI In/Out"): performance data (keys, wheels, sustain, pads in Drum/Chord/Custom layouts, Custom-mode pots/faders), and standalone-mode button CCs. "Responses to requests sent on MIDI Out (USB) are only returned on MIDI In (USB)."
- **DAW port** ("Launchkey MK3 DAW In/Out"): everything in this spec - DAW-mode enable, pad LEDs, session-mode pad presses, pots/faders in DAW modes, buttons, display SysEx, arp/scale control. [PRG p.4, p.6, p.16]
- DIN MIDI OUT transmits performance data only.

macOS (CoreMIDI) names: [UG p.12, Ableton setup] says on Mac the ports appear as `Launchkey MK3 [...] (LKMK3 DAW OUT)` (the port you READ from = CoreMIDI *source*) and `Launchkey MK3 [...] (LKMK3 DAW IN)` (the port you WRITE to = CoreMIDI *destination*); Windows: `MIDIIN2/MIDIOUT2 (LKMK3 MIDI)`. The "[...]" is the model (e.g. "49"). The performance port is "(LKMK3 MIDI OUT/IN)" by analogy - **UNVERIFIED exact macOS string**. Recommended implementation: enumerate sources/destinations and match case-insensitively on `LKMK3` + `DAW` (vs. no `DAW`) rather than hard-coding the full string. Note Novation's own articles are inconsistent about In/Out wording, so match by substring.

Which port each message uses: ALL messages in sections 2-9 go on the **DAW port**, except performance notes (keyboard, mod/pitch wheel, sustain, Drum/Chord pad layouts) which stay on the **MIDI port** even in DAW mode [PRG p.6]. Device Inquiry reply returns on the port family it was sent on (PRG says MIDI Out -> MIDI In; DAW port behaviour **UNVERIFIED** - send on both and listen).

---
## 2. Entering / leaving DAW mode [PRG p.6]

| Action | Bytes (DAW port) |
|---|---|
| Enter DAW mode | `9F 0C 7F` (Ch16 Note On, note 12, vel 127) |
| Exit DAW mode (back to Standalone) | `9F 0C 00` (Note On vel 0; Note Off `8F 0C ..` presumably equivalent: doc says "A Note Off event exits") |
| Enable continuous-control Touch events | `9F 0B 7F` (disable: `9F 0B 00`) - touch = CC on Ch15, see s5/s6 |
| Enable Pot Pickup | `9F 0A 7F` (disable: `9F 0A 00`) |

- Default on entry: Touch events DISABLED, Pot Pickup DISABLED. Send the enable-feature messages AFTER entering DAW mode ("first enter DAW mode, then enable features needed").
- Defaults on entry [PRG p.7]: Pads = Session layout, Pots = Pan, Faders = Volume (49/61/88). "The DAW should initialize each of these areas accordingly" - i.e. you must light pads / name controls yourself.
- Pad colour reset on entry: **UNVERIFIED** (doc silent). Assume LEDs must be (re)painted after entry; clear all 16 pads explicitly.
- Knob/fader behaviour change: in DAW mode, button + non-performance surface elements report on the DAW port only (not MIDI port); pots/faders in Device/Volume/Pan/Send modes send fixed CCs on **Ch16** (s5/s6) instead of the Custom-mode CCs. [PRG p.6, p.9-10]
- Host exit: PRG says the DAW "should exit DAW mode (send 9Fh 0Ch 00h)". Whether the keyboard auto-leaves DAW mode on USB disconnect/app crash: **UNVERIFIED** (doc silent; no keep-alive/heartbeat exists in the document). Exiting also clears the default display [PRG p.14]. Practical: send exit on quit and on SIGTERM/atexit; on power cycle the device boots in Standalone [PRG p.5].

---
## 3. Pads in DAW mode

Physical grid: 2 rows x 8 pads. Top row = row 1, bottom row = row 2 (the "lower left pad" = bottom row, leftmost) [PRG p.12 examples].

### Mode select / report (DAW port, Ch16) [PRG p.7]
`BF 03 <v>` - send to change pad mode; the device also sends it when the user changes mode (incl. holding Device Select).
v: 0 = Custom Mode 0 (listed twice, also 5), 1 = Drum layout, 2 = **Session layout**, 3 = Scale Chords, 4 = User Chords, 5..8 = Custom Mode 0..3 (as printed: 5=CM0, 6=CM1, 7=CM2, 8=CM3), 9 = Device Select, 10 = Navigation.
(Doc prints value 0 and 5 both as "Custom Mode 0" - transcribed verbatim.) Session mode is the DAW-mode default and is what the user gets via Shift menu.

### Note maps (pad -> note) and press messages
| Mode | Channel (press + LED static/flash/pulse) | Top row L->R | Bottom row L->R |
|---|---|---|---|
| **Session** [PRG p.8] | press: Ch1 on DAW port. LED: Ch1 / Ch2 / Ch3 | 96..103 (0x60-0x67) | 112..119 (0x70-0x77) |
| **Drum** [PRG p.9] | press: Ch10 on MIDI port (stays performance). LED: Ch10 / Ch11 / Ch12 | 40,41,42,43,48,49,50,51 (0x28-2B,30-33) | 36,37,38,39,44,45,46,47 (0x24-27,2C-2F) |
| **Device Select** [PRG p.9] | press: Ch1 DAW port; auto-selected while holding Device Select. LED Ch1/2/3 | 64..71 (0x40-47) | 80..87 (0x50-57) |

- Press = Note On (`90`/`9A`), velocity = strike velocity (velocity curve configurable, s9); release = Note On vel 0 (device never sends `8x` Note Off) [PRG p.4]. Polyphonic aftertouch `A0` (Session / Device Select, Ch1) or `AA` (Drum, Ch10) only if Poly Aftertouch is selected in device settings; otherwise channel pressure is not mentioned (**UNVERIFIED**).
- In Session mode pad NOTES: top row 0x60.., bottom 0x70.. => note = 96 + 16*(row) + col, row 0 = top.
- Drum and Chord layouts "remain on the MIDI port" and DAW-mode software controls only their colours (Drum) [PRG p.6, p.9].
- The device is in Session mode by default in DAW mode; if the user switches modes you receive `BF 03 v` - re-light for the new note map or force `BF 03 02` back.

---
## 4. Pad / LED colour control [PRG p.10-13]

Send a Note On (pads, and fader buttons if they are note-addressed) or Control Change (CC-addressed buttons) to the *same index the control reports*; **channel selects the lighting mode, velocity/value selects palette colour**:

| Channel | Meaning |
|---|---|
| Ch1 (`90`/`B0`) | static colour |
| Ch2 (`91`/`B1`) | flashing: alternates between colour A (the static/pulse colour previously set) and B (this message's colour); 50% duty, one period = 1 beat, synced to MIDI clock (120 BPM or last clock if none) |
| Ch3 (`92`/`B2`) | pulsing: dark<->full, one period = 2 beats, synced as above |
| Ch16 (`BF`) | static GREY-SCALE for CC-associated controls only |
| Drum pads only: Ch10 / Ch11 / Ch12 | static / flash / pulse |

Examples (verbatim from [PRG p.12-13]): `90 70 05` lower-left pad static red; `91 60 13` upper-left pad flashing green (flash colour 19; A = existing static); `92 77 2D` lower-right pulsing blue; `90 77 00` colour off (also clears a pulsing colour); `80 77 00` also off.
Palette 0 = off/black. Velocity 0 turns the LED off.

**RGB SysEx for pads: NOT in the MK3 doc.** Only the 128-colour palette is available for pad/button LEDs (the only RGB triplets in the doc are for the start-up animation SysEx, s9). (The Launchpad-style `0x03` RGB LED SysEx is not documented for Launchkey MK3 - treat as unsupported.)

LEDs that are white-only (colour shown as grey): Device Lock, Arm/Select (49/61/88) [PRG p.10]. Buttons with NO LED (colour ignored): Capture MIDI, Quantise, Click, Undo, Play, Stop, Record, Loop, Track Left, Track Right, Device Select, Shift [PRG p.11].

### Palette (index -> approximate sRGB)
Sampled from the doc's palette image (p.11) by pixel; these are the *on-screen illustration* colours, so physical LEDs only approximate them. Index 0 drawn as dark grey = LED off. The grid is 8 columns x 16 rows in index order (0-63 left block, 64-127 right block). Landmarks: 3 = white, 5 = red, 9 = orange, 13 = yellow, 21 = green, 19 (0x13) = muted green (called Green in the doc example), 45 = blue (0x2D, used for blue in example), 53 = magenta/pink.

| idx | hex | RGB approx |
|---|---|---|
| 0 | 0x00 | #606060 |
| 1 | 0x01 | #B4B4B4 |
| 2 | 0x02 | #DDDDDD |
| 3 | 0x03 | #FFFFFF |
| 4 | 0x04 | #FDB3B5 |
| 5 | 0x05 | #F96560 |
| 6 | 0x06 | #D86465 |
| 7 | 0x07 | #AF6569 |
| 8 | 0x08 | #FEF0D4 |
| 9 | 0x09 | #FFB666 |
| 10 | 0x0A | #E19267 |
| 11 | 0x0B | #B37563 |
| 12 | 0x0C | #FEEBA3 |
| 13 | 0x0D | #FCFE67 |
| 14 | 0x0E | #DBDA6A |
| 15 | 0x0F | #B5B26A |
| 16 | 0x10 | #DEFE9E |
| 17 | 0x11 | #C8FE5C |
| 18 | 0x12 | #AADC69 |
| 19 | 0x13 | #86B763 |
| 20 | 0x14 | #C0FEAF |
| 21 | 0x15 | #69F863 |
| 22 | 0x16 | #68DC62 |
| 23 | 0x17 | #6AB168 |
| 24 | 0x18 | #BFFFC5 |
| 25 | 0x19 | #65FD8E |
| 26 | 0x1A | #62E074 |
| 27 | 0x1B | #61B368 |
| 28 | 0x1C | #C1FEC7 |
| 29 | 0x1D | #5FFFC2 |
| 30 | 0x1E | #66DCA2 |
| 31 | 0x1F | #68B586 |
| 32 | 0x20 | #C0FFF3 |
| 33 | 0x21 | #5BFFE7 |
| 34 | 0x22 | #5FE0C2 |
| 35 | 0x23 | #5EB595 |
| 36 | 0x24 | #C5F3FD |
| 37 | 0x25 | #61EFFC |
| 38 | 0x26 | #65C4DA |
| 39 | 0x27 | #62A1AF |
| 40 | 0x28 | #C4DDFF |
| 41 | 0x29 | #5CCAFE |
| 42 | 0x2A | #5FA1E0 |
| 43 | 0x2B | #6083B4 |
| 44 | 0x2C | #A38FFC |
| 45 | 0x2D | #6365FA |
| 46 | 0x2E | #6262D5 |
| 47 | 0x2F | #6365B0 |
| 48 | 0x30 | #C8B3FC |
| 49 | 0x31 | #9D61FD |
| 50 | 0x32 | #8666E6 |
| 51 | 0x33 | #7963B5 |
| 52 | 0x34 | #FBB3FF |
| 53 | 0x35 | #FB68FB |
| 54 | 0x36 | #DC67E0 |
| 55 | 0x37 | #AE67B0 |
| 56 | 0x38 | #FEB3D3 |
| 57 | 0x39 | #FF60C5 |
| 58 | 0x3A | #E05FA0 |
| 59 | 0x3B | #B4608C |
| 60 | 0x3C | #FD7661 |
| 61 | 0x3D | #E8B461 |
| 62 | 0x3E | #DBC267 |
| 63 | 0x3F | #9CA063 |
| 64 | 0x40 | #5DB75C |
| 65 | 0x41 | #61B28B |
| 66 | 0x42 | #608BD8 |
| 67 | 0x43 | #6262FE |
| 68 | 0x44 | #65B5B5 |
| 69 | 0x45 | #8E60F9 |
| 70 | 0x46 | #CBB4C1 |
| 71 | 0x47 | #8E7783 |
| 72 | 0x48 | #FA6261 |
| 73 | 0x49 | #F4FFA2 |
| 74 | 0x4A | #ECFD60 |
| 75 | 0x4B | #CDFF57 |
| 76 | 0x4C | #76DF62 |
| 77 | 0x4D | #5FFFD6 |
| 78 | 0x4E | #64EBFE |
| 79 | 0x4F | #64A2FE |
| 80 | 0x50 | #8E61FC |
| 81 | 0x51 | #CA60FF |
| 82 | 0x52 | #EC8DDD |
| 83 | 0x53 | #A37758 |
| 84 | 0x54 | #FDA55E |
| 85 | 0x55 | #D8FC64 |
| 86 | 0x56 | #D3FE94 |
| 87 | 0x57 | #5EFC65 |
| 88 | 0x58 | #B0FFAA |
| 89 | 0x59 | #CDFBD4 |
| 90 | 0x5A | #BBFCF4 |
| 91 | 0x5B | #CBE4FD |
| 92 | 0x5C | #ACBEF5 |
| 93 | 0x5D | #DBC2FB |
| 94 | 0x5E | #F788FB |
| 95 | 0x5F | #FD65C7 |
| 96 | 0x60 | #FCC05D |
| 97 | 0x61 | #F3EE60 |
| 98 | 0x62 | #E3FE5F |
| 99 | 0x63 | #DBCC5F |
| 100 | 0x64 | #B4A462 |
| 101 | 0x65 | #5EBC77 |
| 102 | 0x66 | #76C58E |
| 103 | 0x67 | #8380A2 |
| 104 | 0x68 | #8588C9 |
| 105 | 0x69 | #CCAA7F |
| 106 | 0x6A | #DC6259 |
| 107 | 0x6B | #FCB09F |
| 108 | 0x6C | #F9BA75 |
| 109 | 0x6D | #FDF38A |
| 110 | 0x6E | #E7F9A0 |
| 111 | 0x6F | #D5EF78 |
| 112 | 0x70 | #7D7D9F |
| 113 | 0x71 | #FDFCD6 |
| 114 | 0x72 | #E1F9E6 |
| 115 | 0x73 | #E6EBF9 |
| 116 | 0x74 | #E4D7FB |
| 117 | 0x75 | #B6B7B2 |
| 118 | 0x76 | #D5D4D2 |
| 119 | 0x77 | #F9FDFA |
| 120 | 0x78 | #E95E61 |
| 121 | 0x79 | #AE5E62 |
| 122 | 0x7A | #7FF85B |
| 123 | 0x7B | #5EB55C |
| 124 | 0x7C | #F5EE59 |
| 125 | 0x7D | #B4A35E |
| 126 | 0x7E | #F1C15D |
| 127 | 0x7F | #C47761 |
---
## 5. Knobs (8 pots) [PRG p.8-9]

- Mode select/report: `BF 09 <v>` (DAW port, Ch16). v: 0 = Custom 0 (also 6), 1 = Volume, 2 = Device, 3 = Pan, 4 = Send-A, 5 = Send-B, 6..9 = Custom Mode 0..3 (as printed). DAW-mode default = Pan (3).
- In ALL of Device/Volume/Pan/Send-A/Send-B the pots send the SAME CCs on **Ch16** (`BF`): pot 1..8 = **CC 21..28 (0x15-0x1C)**. The mode only matters to the LCD/labels, not the CC numbers - the host interprets them by the current mode (or ignores the mode).
- Value type: default display is 0-127 [PRG p.15] and the pots are rotary potentiometers (not endless encoders) -> **absolute 0-127**. An explicit "absolute" statement is **UNVERIFIED** in the PRG text, but no relative mode is documented.
- Touch events (if enabled with `9F 0B 7F`): CC on **Ch15**, same CC number, value 127 touch-on / 0 touch-off, e.g. `BE 15 7F` / `BE 15 00` for pot 1 [PRG p.9].
- Pot Pickup: enable with `9F 0A 7F`. PRG only states the enable/disable flag (default off); the algorithm (value catch / threshold) is not documented -> **UNVERIFIED**.
- Custom modes (0..3) are device-defined (Components), emit on the MIDI port - avoid.
- LCD name/value: SysEx s8 with control index 0x38-0x3F (pots 1-8).

---
## 6. Faders (9) and fader buttons - 49/61/88 only [PRG p.8, p.10]

- Mode select/report: `BF 0A <v>` (DAW port, Ch16). v: 0 = Custom 0 (also 6), 1 = Volume, 2 = Device, 4 = Send-A, 5 = Send-B, 6..9 = Custom 0..3 (no Pan). DAW-mode default = Volume.
- Faders 1-9: **CC 53..61 (0x35-0x3D), Ch16** in Device/Volume/Send-A/Send-B modes (same CCs in every mode). Fader 9 = master in DAWs [UG p.9]. Absolute 0-127.
- Touch events (if enabled): Ch15, same CC, 127/0, e.g. leftmost fader `BE 35 7F` / `BE 35 00` [PRG p.10].
- Fader buttons 1-9: the figure on [PRG p.10] gives **CC 37..45 (0x25-0x2D)**. LED colour: send CC 37..45 on Ch1/2/3 (static/flash/pulse) with a palette value [PRG p.10 "a Control Change matching those described in the reports"]. The PDF does NOT state the channel/values of the fader-button PRESS report; the figure groups them with the Ch16 fader CCs. So presses most likely arrive as CC 37..45 on Ch16, 127 press / 0 release - **UNVERIFIED, confirm on hardware** (listen on all channels).
- Arm/Select button (49/61/88): white LED (grey-scale only) [PRG p.10]; its CC is not named in the PRG. The page-6 figure shows an LED-capable (red) button at CC **108 (0x6C)** top-left of the left button cluster - plausibly Arm/Select, **UNVERIFIED**. In Ableton it toggles fader buttons between Arm and Select [UG p.18].
- LCD name/value: control index 0x50-0x58 (80-88).
- [FL] uses faders CC 71-79 on Ch1 - that is a standalone/MIDI-port mapping, NOT DAW mode.

---
## 7. Buttons in DAW mode [PRG p.6 figure + p.10-11]

All on the **DAW port**. Figure legend: black number = reports on Ch16 (`BF cc v`); red number = Ch1 (LED colouring on Ch1/2/3 for that CC; the PDF legend does not clearly say whether presses of red buttons are also Ch16 - verify). Press = 127, release = 0 (not spelled out in the PRG -> **UNVERIFIED**, consistent with [FL] which triggers on `data2 > 0`).

| Button | CC | hex | LED? | Name source |
|---|---|---|---|---|
| Capture MIDI | 74 | 4A | no | figure + [FL] `capmidi=74` |
| Quantise | 75 | 4B | no | [FL] |
| Click (metronome) | 76 | 4C | no | [FL] |
| Undo | 77 | 4D | no | [FL] |
| Play | 115 | 73 | no | [FL] `play_button=115` |
| Stop | 116 | 74 | no | [FL] |
| Record | 117 | 75 | no | [FL] |
| Loop | 118 | 76 | no | [FL] |
| Track Left | 102 | 66 | no | figure + [FL] `prev=102` |
| Track Right | 103 | 67 | no | [FL] `next=103` |
| Device Select | 51 | 33 | no (PRG p.11) | [FL] |
| Device Lock | 52 | 34 | yes, white only (PRG p.10) | [FL] |
| Scene Launch ">" | 104 | 68 | yes (red in fig.) | figure, right of pad row 1; [UG p.9 #17]; [FL] |
| Stop/Solo/Mute | 105 | 69 | yes (red in fig.) | figure, right of pad row 2; [UG p.9 #18]; [FL] |
| Pad-row arrow up | 106 | 6A | black in fig. | figure, left of pad row 1; [FL] `UpArrow=106` |
| Pad-row arrow down | 107 | 6B | black in fig. | [FL] |
| unlabelled, LED-capable | 108 | 6C | yes | figure only; maybe Arm/Select - **UNVERIFIED** |
| Shift | none documented | | no | handled internally; **UNVERIFIED** whether any event is sent |
| Settings, "...", Fixed Chord, Arp, Scale, Octave +/- | not reported in DAW mode | | | device-internal |

Caveat: PRG p.6 says "Scene Up and Scene Down buttons also report back CC 68h (104) and 69h (105) on Channel 16" for Mini MK3 script compatibility, which conflicts with the figure's layout (104/105 right of pads, 106/107 left). Verify the physical-to-CC mapping of 104-107 on hardware. There are no dedicated "Function" buttons; the programmable-looking ones are Capture MIDI/Quantise/Click/Undo (CC 74-77).

---
## 8. Display (16x2 LCD) [PRG p.13-15]

The MK3 has a 16x2 character LCD, writable in DAW mode. Header: `F0 00 20 29 02 0F` for 25/37/49/61 (88 uses `... 02 12`).
Priority: default display (lowest, normally blank) < temporary display (5 s after touching a control) < menu display (highest). Data is buffered and shown when that level is next due.
Characters: 0x20-0x7E ASCII; 0x11 = next byte is ISO-8859-2 upper-bank char (add 0x80); avoid other control chars and 0x7F.

| Function | SysEx (25/37/49/61) |
|---|---|
| Set default display row | `F0 00 20 29 02 0F 04 <row> <chars...> F7` (padded to 16, excess ignored; cancels a temporary display; cleared on DAW exit) |
| Clear default display | `F0 00 20 29 02 0F 06 F7` (preferred; also tells the unit the DAW gives up the default display) |
| Parameter NAME (top row of temp display) | `F0 00 20 29 02 0F 07 <ctl> <chars...> F7` |
| Parameter VALUE (bottom row of temp display) | `F0 00 20 29 02 0F 08 <ctl> <chars...> F7` |

`<ctl>`: 0x38-0x3F (56-63) = pots 1-8; 0x50-0x58 (80-88) = faders 1-9. Updating while the temp display is active takes effect immediately without extending the 5 s. If no value is sent the device shows 0-127. `<row>` values (0/1 vs 1/2) are **UNVERIFIED** (not enumerated in the PDF) - test.

---
## 9. Identification, keep-alive, configuration, other

- Device Inquiry [PRG p.4]: send `F0 7E 7F 06 01 F7`. Reply (application): `F0 7E 00 06 02 00 20 29 <dev> 01 00 00 <v1 v2 v3 v4> F7`; (bootloader) `... <dev> 11 00 00 <v..>`. `<dev>`: 0x34 (52) = 25, 0x35 (53) = 37, **0x36 (54) = 49**, 0x37 (55) = 61, 0x40 (64) = 88. Version = 4 bytes, one decimal digit each. The 88 uses SysEx header `02 12` instead of `02 0F`.
- Keep-alive / heartbeat: **none documented** (no active-sensing requirement).
- Velocity curve [PRG p.17]: `F0 00 20 29 02 0F 02 <target> <curve> F7`; target 0 keys, 1 pads; curve 0 soft, 1 medium, 2 hard, 3 fixed.
- Start-up animation [PRG p.18]: `F0 00 20 29 02 0F 78 <interval x2ms> <r g b>... F7` (<=56 triplets, 0-127).
- Arpeggiator [PRG p.16] (Ch1 CCs, DAW port): 110 on/off, 85 type 0-6, 86 rate 0-7, 87 octave 0-3, 88 latch, 89 gate 0-99, 90 swing 34-94, 91 rhythm 0-4, 92 mutate, 93 deviate.
- Scale mode [PRG p.17] (Ch16 CCs): 14 on/off, 15 type 0-7, 16 root 0-11.

---
## 10. Differences vs Launchkey Mini MK3 and MK4 (do not mix)

- **Mini MK3**: no faders/fader buttons, 16 pads, 8 pots; separate Programmer's Reference. Scene up/down on CC 104/105 (hence the MK3 compat sentence). **Not read from official doc here -> UNVERIFIED.** Reverse-engineered reference (not opened): https://github.com/giezu/LaunchkeyMiniMK3.
- **MK4** (2024): a different Programmer's Reference (cdm.link/2024/08/novation-launchkey-programmers-reference-guide/ , not read), different SysEx; do not reuse MK3 bytes.
- Within MK3: the 88 differs only by SysEx byte 0x12 and dev_type 0x40; 25/37 lack faders, fader buttons and Arm/Select.

---
## 11. Suggested mapping: 4-layer step-sequencer synth

Protocol constraints: 16 pads in 2 rows x 8 (Session: Ch1 notes 96-103 top / 112-119 bottom) = one natural 16-step bar; palette-only LEDs with static/flash/pulse; Track buttons CC 102/103 (no LED); arrows CC 106/107; pots CC 21-28 absolute Ch16; faders CC 53-61; fader buttons CC 37-45 with LEDs.

- **Pads = 16 steps**: top row = steps 1-8, bottom row = 9-16 (read like text). Off (0) = inactive; layer colour static (e.g. 21 green, 45 blue, 5 red, 13 yellow) = active step; playhead: pulse (Ch3) is clock-synced only, so for an exact playhead repaint the current step white (3) yourself each tick.
- **Track ◄ ► (CC 102/103) = layer select** (4 layers); repaint pads; show layer name on the default display.
- **▲ ▼ (CC 106/107) = step page** (16->32/64 steps); show page on LCD.
- **Play 115 = start/stop, Stop 116, Record 117 = record/overdub, Loop 118, Click 76 = metronome, Undo 77.**
- **Pots CC 21-28 = 8 params of selected layer**, named via SysEx 07/08 (ctl 0x38-0x3F); enable Pot Pickup (`9F 0A 7F`) but also implement app-side soft takeover (pickup algorithm UNVERIFIED).
- **Faders 1-4 (CC 53-56) = layer levels, 5-8 (CC 57-60) = reverb sends, 9 (CC 61) = master**; keep Volume mode (`BF 0A 01`).
- **Fader buttons (CC 37-45) = per-layer mute** (1-4; rest spare): LED via CC on Ch1 (bright = unmuted, 0 = muted). Listen for press on any channel (unverified).
- Force Session mode: on `BF 03 v` with v != 2 reply `BF 03 02` (or repaint for the reported layout).
- Startup: open DAW ports -> `F0 7E 7F 06 01 F7` -> `9F 0C 7F` -> `BF 03 02`, `BF 09 03`, `BF 0A 01` -> `9F 0A 7F` -> clear and paint pads -> LCD names. Shutdown: `F0 00 20 29 02 0F 06 F7`, clear pad LEDs, `9F 0C 00`.

---
## 12. UNVERIFIED summary
macOS string of the performance port; colour reset / auto-exit on entry/disconnect; fader-button and button press channel+values (figure only); meaning of CC 108; naming of CC 104-107 vs the "Scene Up/Down" sentence; `<row>` byte values; pot-pickup algorithm; explicit "absolute" for pots; Shift event; DAW-port inquiry reply; channel-pressure behaviour; physical LED colours vs palette illustration; Mini MK3 / MK4 specifics.
