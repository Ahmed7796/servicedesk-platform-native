import React, { useState } from 'react';
import { Button, StyleSheet, Text, TextInput, View } from 'react-native';

type Props = {
  submitting: boolean;
  onApprove: (comment: string) => void;
  onReject: (reason: string) => void;
};

/** One text box: an optional comment for Approve, the required reason for Reject. */
export function DecisionPanel({ submitting, onApprove, onReject }: Props) {
  const [text, setText] = useState('');
  return (
    <View style={styles.panel}>
      <Text accessibilityRole="header" style={styles.heading}>
        Your decision
      </Text>
      <TextInput
        testID="decision-input"
        accessibilityLabel="Comment or reason"
        placeholder="Comment or reason"
        value={text}
        onChangeText={setText}
        maxLength={500}
        multiline
        editable={!submitting}
        style={styles.input}
      />
      <View style={styles.buttons}>
        <Button testID="approve-button" title="Approve" disabled={submitting} onPress={() => void onApprove(text)} />
        <Button testID="reject-button" title="Reject" color="#b00020" disabled={submitting || text.trim() === ''} onPress={() => void onReject(text)} />
      </View>
    </View>
  );
}

const styles = StyleSheet.create({
  panel: { gap: 8 },
  heading: { fontWeight: '600', fontSize: 16 },
  input: { borderWidth: 1, borderColor: '#999', borderRadius: 4, padding: 8, minHeight: 60 },
  buttons: { flexDirection: 'row', gap: 12 },
});
