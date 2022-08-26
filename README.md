# NotEnoughAV1Encodes


---


## NOTICE: This fork is just an attempt to add SVT-HEVC to NEAV1E.
Currently, it works only through FFmpeg and need a FFmpeg version compiled with SVT-HEVC (replace the original in NEAV1E apps folder)

Known issue: if you update FFmpeg using app updater, it will break SVT-HEVC support as the original version does not have builtin libsvt_hevc.

---


#### NEAV1E is a GUI for AV1 encoders - aomenc, rav1e, svt-av1 & vp9. 

A tool to make encoding faster and easier for AV1 encoders.

![alt text](https://i.imgur.com/EcF3P1l.png "Darkmode")


---

## 🔬 How does this program work?
1. This program will split the given video file into chunks (scene based splitting or equal chunks).
2. After splitting, it will encode the chunks with n-amount of workers. 
3. When finished, it will merge the encoded files to a single video file.


## ![alt text](https://i.imgur.com/Ql4lP4E.png) Releases [![Build status](https://ci.appveyor.com/api/projects/status/f3wd2kr5i8eofj88/branch/master?svg=true)](https://ci.appveyor.com/project/Alkl/notenoughav1encodes/branch/master)

#### Stable Builds: [Releases] No stable release yet

#### Testing Builds: [AppVeyor](https://ci.appveyor.com/project/manzing/notenoughav1encodes/branch/SVT-HEVC-FFmpeg-Support/artifacts)

## 🐧 Linux (Wine)
NEAV1E is written in .NET Core, which is in theory compatible with other platforms.

However as the WPF UI Framework is used, it is not compatible with the native .NET Core Version of Linux.

It is possible to run NEAV1E with the Wine compatibility layer (Wine is not an Emulator!).

The following YouTube Video demonstrates that: [Run WPF Applications on Linux (Manjaro)](https://www.youtube.com/watch?v=u1PWRYLuiNQ)


## 📽 Encoders

NEAV1E supports the following encoders:

- aomenc / libaom
- rav1e / librav1e
- svt-av1 / libsvt-av1
- libvpx-vp9

### 🎉 Special Thanks To
- [@wcxu21](https://github.com/wcxu21) for Chinese Translations!
- Nonami for Russian Translations!
- ieno for Japanese Translations!
- All other wonderful people reporting Bugs and being so patient!

---

#### 📬 Contacting me
You can find me on the unofficial [AV1 Discord](https://discord.gg/HSBxne3) or on the [NEAV1E Discord](https://discord.gg/yG27ArHBFe)
