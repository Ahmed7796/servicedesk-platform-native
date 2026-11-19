import React from 'react';
import { ActivityIndicator, Button, ScrollView, StyleSheet, Text, View } from 'react-native';
import type { NativeStackScreenProps } from '@react-navigation/native-stack';
import type { ServiceRequestDetail } from '../api/generated/servicedesk';
import type { RootStackParamList } from '../navigation/types';
import { positionLabel } from '../api/positions';
import { AuditTrail } from '../components/AuditTrail';
import { DecisionPanel } from '../components/DecisionPanel';
import { TriageSuggestionBlock } from '../components/TriageSuggestionBlock';
import { useRequestDetail } from './useRequestDetail';
import { useDecision } from './useDecision';

type Props = NativeStackScreenProps<RootStackParamList, 'RequestDetail'>;

export function RequestDetailScreen({ route, navigation }: Props) {
  const { detail, loading, error, reload } = useRequestDetail(route.params.requestId);
  const decision = useDecision(route.params.requestId, {
    reload,
    onDone: () => navigation.popTo('PendingApprovals', { refresh: Date.now() }),
  });

  if (!detail) {
    return (
      <View testID="request-detail-screen" style={styles.center}>
        {loading ? <ActivityIndicator testID="detail-loading" /> : null}
        {error ? (
          <>
            <Text testID="detail-error" accessibilityRole="alert" accessibilityLiveRegion="polite" style={styles.error}>
              {error}
            </Text>
            <Button title="Try again" onPress={() => void reload()} />
          </>
        ) : null}
      </View>
    );
  }

  return (
    <ScrollView testID="request-detail-screen" contentContainerStyle={styles.content}>
      {error ? (
        <Text testID="detail-error" accessibilityRole="alert" accessibilityLiveRegion="polite" style={styles.error}>
          {error}
        </Text>
      ) : null}
      <Text accessibilityRole="header" style={styles.title}>
        {detail.title}
      </Text>
      <Text>{detail.requesterName}</Text>
      <Text testID="detail-status">Status: {detail.status}</Text>
      <Text>{detail.description}</Text>
      <View style={styles.steps}>
        <Text accessibilityRole="header" style={styles.heading}>
          Approval steps
        </Text>
        {detail.steps.map((s) => (
          <Text key={s.order} testID={`step-${s.order}`}>
            {s.order}. {positionLabel(s.position)} · {s.decision}
            {s.comment ? ` · "${s.comment}"` : ''}
          </Text>
        ))}
      </View>
      <TriageSuggestionBlock triage={detail.triage} />
      {decision.error ? (
        <Text testID="decision-error" accessibilityRole="alert" accessibilityLiveRegion="polite" style={styles.error}>
          {decision.error}
        </Text>
      ) : null}
      {canDecide(detail) ? <DecisionPanel submitting={decision.submitting} onApprove={decision.approve} onReject={decision.reject} /> : null}
      <AuditTrail entries={detail.auditTrail} />
    </ScrollView>
  );
}

/** The contract has no "can decide" field; this mirrors PendingItem. The server stays the authority (403/409). */
function canDecide(d: ServiceRequestDetail): boolean {
  return (d.status === 'Submitted' || d.status === 'InReview') && d.steps.some((s) => s.decision === 'Pending');
}

const styles = StyleSheet.create({
  center: { flex: 1, alignItems: 'center', justifyContent: 'center', padding: 16, gap: 12 },
  content: { padding: 16, gap: 12 },
  title: { fontSize: 20, fontWeight: '700' },
  heading: { fontWeight: '600', fontSize: 16 },
  steps: { gap: 4 },
  error: { color: '#b00020' },
});
