import React from 'react';
import { ActivityIndicator, FlatList, RefreshControl, StyleSheet, Text, View } from 'react-native';
import type { NativeStackScreenProps } from '@react-navigation/native-stack';
import type { RootStackParamList } from '../navigation/types';
import { PendingItemRow } from '../components/PendingItemRow';
import { usePendingApprovals } from './usePendingApprovals';

type Props = NativeStackScreenProps<RootStackParamList, 'PendingApprovals'>;

export function PendingApprovalsScreen({ navigation }: Props) {
  const { items, loading, refreshing, error, refresh } = usePendingApprovals();

  if (loading && items === null) {
    return (
      <View testID="pending-approvals-screen" style={styles.center}>
        <ActivityIndicator testID="pending-loading" />
      </View>
    );
  }

  return (
    <View testID="pending-approvals-screen" style={styles.container}>
      {error ? (
        <Text testID="pending-error" accessibilityRole="alert" accessibilityLiveRegion="polite" style={styles.error}>
          {error}
        </Text>
      ) : null}
      <FlatList
        testID="pending-list"
        data={items ?? []}
        keyExtractor={(item) => item.requestId}
        renderItem={({ item }) => (
          <PendingItemRow item={item} onPress={(requestId) => navigation.navigate('RequestDetail', { requestId })} />
        )}
        refreshControl={<RefreshControl refreshing={refreshing} onRefresh={refresh} />}
        ListEmptyComponent={
          items !== null ? (
            <Text testID="pending-empty" style={styles.empty}>
              Nothing is waiting for your decision.
            </Text>
          ) : undefined
        }
        contentContainerStyle={items?.length === 0 ? styles.emptyContainer : undefined}
      />
    </View>
  );
}

const styles = StyleSheet.create({
  container: { flex: 1 },
  center: { flex: 1, alignItems: 'center', justifyContent: 'center' },
  error: { color: '#b00020', padding: 16 },
  empty: { textAlign: 'center', color: '#555', padding: 24 },
  emptyContainer: { flexGrow: 1, justifyContent: 'center' },
});
