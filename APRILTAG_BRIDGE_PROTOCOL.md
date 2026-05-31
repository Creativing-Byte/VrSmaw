# AprilTag Bridge Protocol

El receptor en Unity escucha por UDP en el puerto `9200` por defecto.

La recomendacion del proyecto es que el detector envie la pose del marcador relativa a la camara del visor (`CameraLocal`). De esa forma Unity reconstruye la pose mundial usando la `Main Camera`.

## Formato CSV recomendado

```text
timestampMs,markerId,px,py,pz,qx,qy,qz,qw,quality,sizeM,frame
```

Ejemplo:

```text
1715001234567,10,0.042,-0.018,0.455,0.0,0.0,0.7071,0.7071,0.96,0.08,CameraLocal
```

## Campos

- `timestampMs`: tiempo de la deteccion.
- `markerId`: id del tag.
- `px, py, pz`: posicion del marcador.
- `qx, qy, qz, qw`: rotacion del marcador en cuaternion.
- `quality`: confianza o score de la deteccion.
- `sizeM`: tamano fisico del tag en metros.
- `frame`: `World`, `CameraLocal` o `XROriginLocal`.

## JSON compatible

```json
{
  "timestampMs": 1715001234567,
  "id": 10,
  "px": 0.042,
  "py": -0.018,
  "pz": 0.455,
  "qx": 0.0,
  "qy": 0.0,
  "qz": 0.7071,
  "qw": 0.7071,
  "quality": 0.96,
  "sizeM": 0.08,
  "frame": "CameraLocal"
}
```

## En Unity

- `AprilTagTrackingReceiver` recibe y guarda las poses.
- `AprilTagTrackedObject` toma un `markerId`, resuelve la pose mundial y aplica offsets.
- `AprilTagBoardTracker` puede fusionar varios tags fisicos en una sola pose virtual mas estable.

## Recomendacion fisica

- Pinza: board rigido con `2` tags `TagStandard41h12` de `6` a `8 cm`.
- Piezas: base con `1` o `2` tags de `8` a `12 cm`.
- Evitar poner el tag junto a la punta del electrodo; es mejor desplazarlo a una bandera lateral o superior.

## IDs recomendados en este repo

- `10` y `11`: board fisica de la pinza
- `100`: pinza fusionada en Unity
- `1`, `2`, `3`, `4`: piezas de practica
