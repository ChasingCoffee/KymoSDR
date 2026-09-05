/* SPDX-License-Identifier: GPL-2.0-or-later
 * Offline lifecycle boundary extracted from CMCreateCMaster/CreateRadio.
 * Not a radio transport: no network units are linked, and no packet input or
 * transmit operation is exposed by this API. The full 8/5/2/1 topology remains.
 */
#include "cmcomm.h"
#include "cm_session.h"
#include "radio_ports.h"

extern void SetRadioStructure(int, int, int, int, int, int *, int *, int, int, int);
extern void set_cmdefault_rates(int *, int, int *, int *);
static volatile LONG command_busy;
static int stage;
static int scope_creates, play_creates, record_creates;

static void __stdcall create_scope(int id) { (void)id; ++scope_creates; }
static void __stdcall create_play(int id) { (void)id; ++play_creates; }
static void __stdcall create_record(int id) { (void)id; ++record_creates; }
static void __stdcall push_vox(int id, int active) { (void)id; (void)active; }
static void discard_samples(int id, int count, double *samples)
{ (void)id; (void)count; (void)samples; }

static int enter_command(void)
{
    /* Bit-test-and-set returns the previous bit on both platforms. */
    return !InterlockedBitTestAndSet(&command_busy, 0);
}
static void leave_command(void) { InterlockedBitTestAndReset(&command_busy, 0); }
static void close_stages(void)
{
    if (stage >= 1)
        for (int i = 0; i < pcm->cmSTREAM; ++i) stop_cmbuffs(i);
    if (stage >= 3) destroy_sync();
    if (stage >= 2) destroy_pipe();
    if (stage >= 1) destroy_cmaster();
    stage = 0;
    /* Clear callback addresses only after every owner has stopped. */
    memset(pcm, 0, sizeof(*pcm));
    memset(ppip, 0, sizeof(*ppip));
    memset(psyn, 0, sizeof(*psyn));
}
CM_API int ThetisCmOpen(int abi, int rx_rate, int audio_mode, int allow_transmit,
                       cm_checkpoint checkpoint, void *context)
{
    if (abi != 1 || allow_transmit != 0) return -1;
    if (audio_mode != 0) return -3;
    if (rx_rate != 48000 && rx_rate != 96000 && rx_rate != 192000 &&
        rx_rate != 384000 && rx_rate != 768000 && rx_rate != 1536000) return -1;
    if (!enter_command()) return -2;
    if (stage) { leave_command(); return -2; }
    int spc[] = {2};
    int inbound[] = {240, 240, 240, 240, 240, 720, 240, 240};
    int rates[] = {rx_rate, rx_rate, rx_rate, rx_rate, rx_rate, 48000, rx_rate, rx_rate};
    int rx_out[] = {48000, 48000, 48000, 48000, 48000};
    int tx_out[] = {192000};
    scope_creates = play_creates = record_creates = 0;
    SetRadioStructure(8, 5, 1, 2, 1, spc, inbound, 1536000, 48000, 384000);
    set_cmdefault_rates(rates, 48000, rx_out, tx_out);
    ppip->create_Scope = create_scope;
    ppip->create_WavePlay = create_play;
    ppip->create_WaveRecord = create_record;
    pcm->xmtr[0].pushvox = push_vox;
    pcm->OutboundRx = discard_samples;
    pcm->OutboundTx = discard_samples;
    /* WDSP's legacy constructors fail-fast on allocation/thread failure. The
     * rollback guarantee here is at completed component boundaries, not OOM. */
    for (int next = 1; next <= 3; ++next)
    {
        if (next == 1) create_cmaster();
        if (next == 2) create_pipe();
        if (next == 3) create_sync();
        stage = next;
        int rc = checkpoint ? checkpoint(stage, context) : 0;
        if (rc)
        {
            close_stages();
            leave_command();
            return rc > 0 ? -4 : -5;
        }
    }
    leave_command();
    return 0;
}
CM_API int ThetisCmClose(void)
{
    if (!enter_command()) return -2;
    close_stages();
    leave_command();
    return 0;
}
CM_API int ThetisCmGetState(int32_t *values, int capacity)
{
    if (!values || capacity < 16) return -1;
    if (!enter_command()) return -2;
    int32_t result[] = {1, stage == 3, pcm->cmSTREAM, pcm->cmRCVR, pcm->cmSubRCVR,
        pcm->cmXMTR, pcm->cmSPC[0], pcm->xcm_inrate[0], pcm->audio_outrate,
        pcm->xmtr[0].ch_outrate, cm_owned_threads(), scope_creates, play_creates,
        record_creates, 0, 0};
    memcpy(values, result, sizeof(result));
    leave_command();
    return 16;
}
CM_API int ThetisCmP2PortBase(int discovery_port, int use_relocated_ports)
{
    /* Reserve the existing 18-port discovery/streaming range, without wrap. */
    if (discovery_port < 1 || discovery_port > 65518 ||
        (use_relocated_ports != 0 && use_relocated_ports != 1)) return -1;
    return cm_p2_port_base(discovery_port, use_relocated_ports);
}
