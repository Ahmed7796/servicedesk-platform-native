import React from 'react';
import { StyleSheet, Text, View } from 'react-native';
import type { TriageSuggestion } from '../api/generated/servicedesk';
import { positionLabel } from '../api/positions';

// Advisory only (rule 6): labelled as a suggestion, no actions attached.
export function TriageSuggestionBlock({ triage }: { triage: TriageSuggestion | null }) {
  return (
    <View style={styles.box}>
      <Text accessibilityRole="header" style={styles.heading}>
        Suggestion (advisory, not a decision)
      </Text>
      {triage ? (
        <>
          <Text>{triage.summary}</Text>
          <Text>
            Priority: {triage.priority} · Category: {triage.category}
          </Text>
          <Text>Route: {triage.suggestedRoute.map(positionLabel).join(' → ')}</Text>
          <Text>Confidence: {Math.round(triage.confidence * 100)}%</Text>
        </>
      ) : (
        <Text testID="triage-none">No suggestion yet</Text>
      )}
    </View>
  );
}

const styles = StyleSheet.create({
  box: { gap: 4, padding: 12, backgroundColor: '#f3f3f7', borderRadius: 8 },
  heading: { fontWeight: '600', fontSize: 16 },
});
