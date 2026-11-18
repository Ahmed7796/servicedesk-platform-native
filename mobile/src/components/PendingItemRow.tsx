import React from 'react';
import { Pressable, StyleSheet, Text } from 'react-native';
import type { PendingItem } from '../api/generated/servicedesk';
import { positionLabel } from '../api/positions';
import { formatAge } from '../api/formatAge';

type Props = { item: PendingItem; overdue: boolean; onPress: (requestId: string) => void };

// The copilot text is advisory (rule 6): shown as "Suggested", never as an action.
export function PendingItemRow({ item, overdue, onPress }: Props) {
  const { triage } = item;
  const label =
    `${item.title}, requested by ${item.requesterName}, waiting for ${positionLabel(item.currentStep.position)}` +
    (triage ? ', has a suggestion' : '') +
    (overdue ? ', overdue' : '');
  return (
    <Pressable
      testID={`pending-row-${item.requestId}`}
      accessibilityRole="button"
      accessibilityLabel={label}
      onPress={() => onPress(item.requestId)}
      style={styles.row}
    >
      {overdue ? (
        <Text testID="overdue-badge" accessibilityLabel="Overdue" style={styles.badge}>
          Overdue
        </Text>
      ) : null}
      <Text style={styles.title}>{item.title}</Text>
      <Text style={styles.meta}>
        {item.requesterName} · waiting for {positionLabel(item.currentStep.position)} · {formatAge(item.createdAt)}
      </Text>
      {triage ? (
        <>
          <Text testID="triage-summary" numberOfLines={3} style={styles.triage}>
            Suggested: {triage.summary}
          </Text>
          <Text style={styles.meta}>Suggested priority: {triage.priority}</Text>
        </>
      ) : (
        <Text testID="triage-none" style={styles.meta}>
          No suggestion yet
        </Text>
      )}
    </Pressable>
  );
}

const styles = StyleSheet.create({
  row: { padding: 16, borderBottomWidth: StyleSheet.hairlineWidth, borderColor: '#bbb', gap: 4 },
  badge: {
    alignSelf: 'flex-start', backgroundColor: '#b00020', color: '#fff', fontSize: 12, fontWeight: '700',
    paddingHorizontal: 8, paddingVertical: 2, borderRadius: 4, overflow: 'hidden',
  },
  title: { fontSize: 16, fontWeight: '600' },
  meta: { fontSize: 13, color: '#555' },
  triage: { fontSize: 14, color: '#333' },
});
