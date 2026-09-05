/* SPDX-License-Identifier: GPL-2.0-or-later
 * Explicit no-device backend, on Windows as well as POSIX. No cmASIO DLL,
 * PortAudio initialization, device enumeration or fallback to a default device.
 */
#include "cmcomm.h"
void create_cmasio(void) {}
void destroy_cmasio(void) {}
long cm_asioStart(int protocol) { (void)protocol; return -1; }
long cm_asioStop(void) { return 0; }
void asioIN(double *input) { (void)input; }
