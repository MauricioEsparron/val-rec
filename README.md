# VAL Rec

Grabador de pantalla para Windows (WPF, .NET 8) con editor de colores y filtros (misma lógica que «Corrección de color» de OBS, nitidez y desenfoque de movimiento).

## Compilar

Necesitas el SDK de .NET 8 y `ffmpeg.exe` en `tools\ffmpeg.exe` (no incluido en el repo).

```
dotnet build src\ValRec.csproj -c Release -o src\bin\stage
```

El icono es `src\gato.ico`.
