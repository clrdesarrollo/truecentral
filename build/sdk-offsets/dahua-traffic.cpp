// Imprime los desplazamientos (offsetof/sizeof) de los eventos de tránsito del
// NetSDK de Dahua que lee TrueCentralVms.Drivers.Dahua (NetSdk.Traffic).
// Compilar con MSVC x64 (las DLL del SDK son x64) y como C++ (el header usa bool):
//   "C:\Program Files\Microsoft Visual Studio\18\Community\VC\Auxiliary\Build\vcvars64.bat"
//   cl /nologo /W0 /EHsc /TP /I"Resources\General_NetSDK_...\Include\Common" build\sdk-offsets\dahua-traffic.cpp
// Si cambia la versión del SDK, regenerar y comparar con NetSdk.Traffic.
#include <windows.h>
#include <stdio.h>
#include <stddef.h>
#include "dhnetsdk.h"

#define P(T, f) printf("%-45s %6zu  (%zu)\n", #T "." #f, offsetof(T, f), sizeof(((T*)0)->f))
#define S(T) printf("%-45s %6zu\n", "sizeof " #T, sizeof(T))

int main(void) {
    S(DH_MSG_OBJECT);
    P(DH_MSG_OBJECT, nObjectID); P(DH_MSG_OBJECT, szObjectType); P(DH_MSG_OBJECT, nConfidence);
    P(DH_MSG_OBJECT, nAction); P(DH_MSG_OBJECT, BoundingBox); P(DH_MSG_OBJECT, Center);
    P(DH_MSG_OBJECT, nPolygonNum); P(DH_MSG_OBJECT, Contour); P(DH_MSG_OBJECT, rgbaMainColor);
    P(DH_MSG_OBJECT, szText); P(DH_MSG_OBJECT, szObjectSubType); P(DH_MSG_OBJECT, bPicEnble); P(DH_MSG_OBJECT, stPicInfo);
    S(DH_PIC_INFO); P(DH_PIC_INFO, dwOffSet); P(DH_PIC_INFO, dwFileLenth);
    S(DH_RECT); S(NET_TIME_EX);
    P(NET_TIME_EX, dwYear); P(NET_TIME_EX, dwMonth); P(NET_TIME_EX, dwDay); P(NET_TIME_EX, dwHour); P(NET_TIME_EX, dwMinute); P(NET_TIME_EX, dwSecond);
    S(DEV_EVENT_TRAFFIC_TRAFFICCAR_INFO);
    P(DEV_EVENT_TRAFFIC_TRAFFICCAR_INFO, szPlateNumber); P(DEV_EVENT_TRAFFIC_TRAFFICCAR_INFO, szPlateType);
    P(DEV_EVENT_TRAFFIC_TRAFFICCAR_INFO, szPlateColor); P(DEV_EVENT_TRAFFIC_TRAFFICCAR_INFO, szVehicleColor);
    P(DEV_EVENT_TRAFFIC_TRAFFICCAR_INFO, nSpeed); P(DEV_EVENT_TRAFFIC_TRAFFICCAR_INFO, szEvent);
    P(DEV_EVENT_TRAFFIC_TRAFFICCAR_INFO, szViolationDesc); P(DEV_EVENT_TRAFFIC_TRAFFICCAR_INFO, nLane);
    P(DEV_EVENT_TRAFFIC_TRAFFICCAR_INFO, fVehicleLength); P(DEV_EVENT_TRAFFIC_TRAFFICCAR_INFO, szVehicleSign);
    P(DEV_EVENT_TRAFFIC_TRAFFICCAR_INFO, byDirection);
    S(DEV_EVENT_TRAFFICJUNCTION_INFO);
    P(DEV_EVENT_TRAFFICJUNCTION_INFO, nChannelID); P(DEV_EVENT_TRAFFICJUNCTION_INFO, szName);
    P(DEV_EVENT_TRAFFICJUNCTION_INFO, UTC); P(DEV_EVENT_TRAFFICJUNCTION_INFO, stuObject);
    P(DEV_EVENT_TRAFFICJUNCTION_INFO, nLane); P(DEV_EVENT_TRAFFICJUNCTION_INFO, nSpeed);
    P(DEV_EVENT_TRAFFICJUNCTION_INFO, byVehicleDirection); P(DEV_EVENT_TRAFFICJUNCTION_INFO, stuVehicle);
    P(DEV_EVENT_TRAFFICJUNCTION_INFO, nTriggerType); P(DEV_EVENT_TRAFFICJUNCTION_INFO, stTrafficCar);
    S(DEV_EVENT_TRAFFICGATE_INFO);
    P(DEV_EVENT_TRAFFICGATE_INFO, nChannelID); P(DEV_EVENT_TRAFFICGATE_INFO, szName);
    P(DEV_EVENT_TRAFFICGATE_INFO, UTC); P(DEV_EVENT_TRAFFICGATE_INFO, stuObject);
    P(DEV_EVENT_TRAFFICGATE_INFO, nLane); P(DEV_EVENT_TRAFFICGATE_INFO, nSpeed);
    P(DEV_EVENT_TRAFFICGATE_INFO, stuVehicle);
    return 0;
}
