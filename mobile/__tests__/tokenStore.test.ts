const mockMem = new Map<string, string>();
jest.mock('expo-secure-store', () => ({
  getItemAsync: async (k: string) => mockMem.get(k) ?? null,
  setItemAsync: async (k: string, v: string) => void mockMem.set(k, v),
  deleteItemAsync: async (k: string) => void mockMem.delete(k),
}));
import { tokenStore } from '../src/auth/tokenStore';

describe('tokenStore', () => {
  it('stores, reads and clears the token', async () => {
    expect(await tokenStore.get()).toBeNull();
    await tokenStore.set('jwt');
    expect(await tokenStore.get()).toBe('jwt');
    await tokenStore.clear();
    expect(await tokenStore.get()).toBeNull();
  });
});
