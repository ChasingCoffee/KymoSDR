/* SPDX-License-Identifier: GPL-2.0-or-later */
#ifndef THETIS_CM_THREAD_H
#define THETIS_CM_THREAD_H
#ifdef _WIN32
typedef HANDLE cm_thread;
#else
typedef pthread_t cm_thread;
#endif
cm_thread cm_start_thread(void (*function)(void *), void *argument);
int cm_try_start_thread(cm_thread *thread, void (*function)(void *), void *argument);
void cm_join_thread(cm_thread thread);
void cm_exit_thread(void);
int cm_owned_threads(void);
/* Only for ChannelMaster's single-consumer mixer semaphores. This is not a
 * general Win32 wait-all emulation: no other waiter may consume these tokens. */
void cm_wait_inputs(int count, HANDLE *inputs);
#endif
