# Third-party notices

## FastNoise

`src/SeedLab.WorldGen/Noise/FastNoiseTables.cs` and `src/SeedLab.WorldGen/Noise/FastNoisePort.cs`
reproduce part of FastNoise by Jordan Peck, as Valheim ships it: the gradient and cellular lookup
tables and the cellular and simplex-fractal routines SeedLab needs to reproduce the game's terrain
bit for bit. The upstream project is https://github.com/Auburn/FastNoise_CSharp, released under the
MIT License:

```
MIT License

Copyright (c) 2016 Jordan Peck

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

## Valheim

Valheim is developed by Iron Gate AB and published by Coffee Stain; "Valheim" is a trademark of
Iron Gate AB. SeedLab is an independent project and is not affiliated with or endorsed by Iron Gate
or Coffee Stain. It reimplements the game's world generation from the shipped game code in order to
reproduce it offline, and it contains no game assets: the game data it reads (`data\`) is dumped by
each user from their own copy of the game and is not part of this repository.
