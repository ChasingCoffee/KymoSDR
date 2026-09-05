/* SPDX-License-Identifier: GPL-2.0-or-later */
#ifndef THETIS_RADIO_PORTS_H
#define THETIS_RADIO_PORTS_H
/* Pure part of nativeInitMetis: discovery on a custom port does not, by itself,
 * imply that the hardware accepts a relocated P2 port range. */
static inline int cm_p2_port_base(int discovery_port, int use_relocated_ports)
{
    return use_relocated_ports ? discovery_port + 1 : 1025;
}
#endif
