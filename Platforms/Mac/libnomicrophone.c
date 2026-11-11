#include <stdio.h>
#include <string.h>
#include <dlfcn.h>
#include <mach/mach.h>
#include <IOKit/IOKitLib.h>
#include <CoreAudio/CoreAudio.h>
#include <CoreFoundation/CoreFoundation.h>
#include <unistd.h>

#define DYLD_INTERPOSE(_replacement,_replacee) \
   __attribute__((used)) static struct{ const void* replacement; const void* replacee; } _interpose_##_replacee \
            __attribute__ ((section ("__DATA,__interpose"))) = { (const void*)(unsigned long)&_replacement, (const void*)(unsigned long)&_replacee };

kern_return_t my_IOServiceOpen(io_service_t service, task_port_t owningTask, uint32_t type, io_connect_t *connect) {
    io_name_t className = {0};
    io_name_t entryName = {0};
    IOObjectGetClass(service, className);
    IORegistryEntryGetName(service, entryName);

    if (strstr(className, "Input") != NULL || strstr(entryName, "Input") != NULL ||
        strstr(className, "Microphone") != NULL || strstr(entryName, "Microphone") != NULL ||
        strstr(className, "Capture") != NULL || strstr(entryName, "Capture") != NULL) {
        return 0xe00002e2;
    }

    return IOServiceOpen(service, owningTask, type, connect);
}
DYLD_INTERPOSE(my_IOServiceOpen, IOServiceOpen);

OSStatus my_AudioObjectGetPropertyData(AudioObjectID inObjectID,
                                      const AudioObjectPropertyAddress *inAddress,
                                      UInt32 inQualifierDataSize,
                                      const void *inQualifierData,
                                      UInt32 *ioDataSize,
                                      void *outData) {
    if (inAddress && inAddress->mSelector == 0x64496e20) {
        if (outData && ioDataSize && *ioDataSize >= sizeof(AudioObjectID)) {
            *((AudioObjectID *)outData) = 0;
            *ioDataSize = sizeof(AudioObjectID);
            return noErr;
        }
    }

    return AudioObjectGetPropertyData(inObjectID, inAddress, inQualifierDataSize, inQualifierData, ioDataSize, outData);
}
DYLD_INTERPOSE(my_AudioObjectGetPropertyData, AudioObjectGetPropertyData);

extern int TCCAccessRequest(CFStringRef, CFDictionaryRef, void (^)(bool)) __attribute__((weak_import));
extern int TCCAccessPreflight(CFStringRef, CFDictionaryRef) __attribute__((weak_import));

int my_TCCAccessRequest(CFStringRef service, CFDictionaryRef options, void (^completion)(bool)) {
    if (service) {
        char buf[128] = {0};
        CFStringGetCString(service, buf, sizeof(buf), kCFStringEncodingUTF8);
        if (strstr(buf, "Microphone") || strstr(buf, "AudioCapture")) {
            if (completion) {
                completion(false);
            }
            return 0;
        }
    }

    if (TCCAccessRequest) {
        return TCCAccessRequest(service, options, completion);
    }
    if (completion) completion(false);
    return 0;
}
DYLD_INTERPOSE(my_TCCAccessRequest, TCCAccessRequest);

int my_TCCAccessPreflight(CFStringRef service, CFDictionaryRef options) {
    if (service) {
        char buf[128] = {0};
        CFStringGetCString(service, buf, sizeof(buf), kCFStringEncodingUTF8);
        if (strstr(buf, "Microphone") || strstr(buf, "AudioCapture")) {
            return 1;
        }
    }

    if (TCCAccessPreflight) {
        return TCCAccessPreflight(service, options);
    }
    return 1;
}
DYLD_INTERPOSE(my_TCCAccessPreflight, TCCAccessPreflight);
