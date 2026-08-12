export type MessageDirection = 'VP_TO_BC' | 'BC_TO_VP';
export type MessageStatus = 'RECEIVED' | 'QUEUED' | 'SENT' | 'DROPPED' | 'ERROR';
export interface BridgeMessage { id: number; receivedAt: Date; bufferedAt?: Date; direction: MessageDirection; payload: string; }
