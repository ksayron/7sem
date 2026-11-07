import 'package:flutter/material.dart';
import 'package:flutter/services.dart';

import 'coffee_app.dart';
import 'coffee_repository.dart';
import 'management_screens.dart';

class LoginScreen extends StatefulWidget {
  const LoginScreen({super.key, required this.repository});
  final CoffeeRepository repository;

  @override
  State<LoginScreen> createState() => _LoginScreenState();
}

class _LoginScreenState extends State<LoginScreen> {
  final _form = GlobalKey<FormState>();
  final _username = TextEditingController();
  final _password = TextEditingController();
  final _usernameFocus = FocusNode();
  final _passwordFocus = FocusNode();
  bool _obscure = true;
  bool _busy = false;
  String? _error;

  @override
  void dispose() {
    _username.dispose();
    _password.dispose();
    _usernameFocus.dispose();
    _passwordFocus.dispose();
    super.dispose();
  }

  Future<void> _signIn() async {
    if (_busy) return;
    setState(() => _error = null);
    if (!_form.currentState!.validate()) {
      (_username.text.trim().isEmpty ? _usernameFocus : _passwordFocus)
          .requestFocus();
      return;
    }
    FocusScope.of(context).unfocus();
    setState(() => _busy = true);
    try {
      await widget.repository.signIn(_username.text, _password.text);
      TextInput.finishAutofillContext();
    } on SignInException catch (error) {
      if (mounted) {
        setState(() => _error = error.toString());
        _passwordFocus.requestFocus();
      }
    } catch (_) {
      if (mounted) {
        setState(() => _error = 'Could not sign in. Please try again.');
      }
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    body: SafeArea(
      child: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 460),
          child: SingleChildScrollView(
            padding: const EdgeInsets.all(24),
            keyboardDismissBehavior: ScrollViewKeyboardDismissBehavior.onDrag,
            child: AutofillGroup(
              child: Form(
                key: _form,
                autovalidateMode: AutovalidateMode.onUserInteraction,
                child: Column(
                  crossAxisAlignment: CrossAxisAlignment.stretch,
                  children: [
                    const Align(
                      alignment: Alignment.centerLeft,
                      child: CircleAvatar(
                        radius: 32,
                        backgroundColor: coffeeCard,
                        child: Icon(
                          Icons.coffee_outlined,
                          color: coffeeOrange,
                          size: 32,
                        ),
                      ),
                    ),
                    const SizedBox(height: 24),
                    const Text(
                      'Coffee Shop',
                      style: TextStyle(color: coffeeOrange, fontSize: 16),
                    ),
                    const SizedBox(height: 8),
                    const Text(
                      'Welcome back',
                      style: TextStyle(
                        fontSize: 32,
                        fontWeight: FontWeight.w600,
                      ),
                    ),
                    const SizedBox(height: 32),
                    TextFormField(
                      key: const Key('login-username'),
                      controller: _username,
                      focusNode: _usernameFocus,
                      enabled: !_busy,
                      autofillHints: const [AutofillHints.username],
                      autocorrect: false,
                      textCapitalization: TextCapitalization.none,
                      textInputAction: TextInputAction.next,
                      decoration: const InputDecoration(
                        labelText: 'Username',
                        prefixIcon: Icon(Icons.person_outline),
                        border: OutlineInputBorder(),
                      ),
                      validator: (value) =>
                          value == null || value.trim().isEmpty
                          ? 'Enter your username.'
                          : null,
                      onFieldSubmitted: (_) => _passwordFocus.requestFocus(),
                      onChanged: (_) {
                        if (_error != null) setState(() => _error = null);
                      },
                    ),
                    const SizedBox(height: 20),
                    TextFormField(
                      key: const Key('login-password'),
                      controller: _password,
                      focusNode: _passwordFocus,
                      enabled: !_busy,
                      obscureText: _obscure,
                      autofillHints: const [AutofillHints.password],
                      autocorrect: false,
                      enableSuggestions: false,
                      textInputAction: TextInputAction.done,
                      decoration: InputDecoration(
                        labelText: 'Password',
                        prefixIcon: const Icon(Icons.lock_outline),
                        border: const OutlineInputBorder(),
                        suffixIcon: IconButton(
                          tooltip: _obscure ? 'Show password' : 'Hide password',
                          onPressed: _busy
                              ? null
                              : () => setState(() => _obscure = !_obscure),
                          icon: Icon(
                            _obscure
                                ? Icons.visibility_outlined
                                : Icons.visibility_off_outlined,
                          ),
                        ),
                      ),
                      validator: (value) => value == null || value.isEmpty
                          ? 'Enter your password.'
                          : null,
                      onFieldSubmitted: (_) => _signIn(),
                      onChanged: (_) {
                        if (_error != null) setState(() => _error = null);
                      },
                    ),
                    if (_error != null) ...[
                      const SizedBox(height: 16),
                      Semantics(
                        liveRegion: true,
                        child: Text(
                          _error!,
                          key: const Key('login-error'),
                          style: TextStyle(
                            color: Theme.of(context).colorScheme.error,
                          ),
                        ),
                      ),
                    ],
                    const SizedBox(height: 24),
                    FilledButton(
                      key: const Key('sign-in'),
                      style: FilledButton.styleFrom(
                        minimumSize: const Size(48, 52),
                        foregroundColor: coffeeBlack,
                      ),
                      onPressed: _busy ? null : _signIn,
                      child: _busy
                          ? const SizedBox.square(
                              dimension: 24,
                              child: CircularProgressIndicator(
                                strokeWidth: 2,
                                semanticsLabel: 'Signing in',
                              ),
                            )
                          : const Text('Sign in'),
                    ),
                    const SizedBox(height: 24),
                    const Card(
                      margin: EdgeInsets.zero,
                      child: ExpansionTile(
                        title: Text('Demo accounts'),
                        subtitle: Text('Local access · no internet needed'),
                        childrenPadding: EdgeInsets.fromLTRB(16, 0, 16, 20),
                        expandedCrossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          SelectableText(
                            'Emma · user\nLogin: emma · Password: emma123\n\n'
                            'Daniel · user\nLogin: daniel · Password: daniel123\n\n'
                            'Alex · admin\nLogin: admin · Password: admin123',
                            style: TextStyle(height: 1.6),
                          ),
                        ],
                      ),
                    ),
                  ],
                ),
              ),
            ),
          ),
        ),
      ),
    ),
  );
}

class ProfileScreen extends StatefulWidget {
  const ProfileScreen({super.key, required this.repository});
  final CoffeeRepository repository;

  @override
  State<ProfileScreen> createState() => _ProfileScreenState();
}

class _ProfileScreenState extends State<ProfileScreen> {
  bool _busy = false;

  Future<void> _signOut() async {
    if (_busy) return;
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Sign out?'),
        content: const Text(
          'Your favorites will stay saved. Sign in again to access your account.',
        ),
        actions: [
          TextButton(
            key: const Key('cancel-sign-out'),
            onPressed: () => Navigator.pop(context, false),
            child: const Text('Stay signed in'),
          ),
          FilledButton(
            key: const Key('confirm-sign-out'),
            onPressed: () => Navigator.pop(context, true),
            child: const Text('Sign out'),
          ),
        ],
      ),
    );
    if (confirmed != true || !mounted) return;
    setState(() => _busy = true);
    try {
      await widget.repository.signOut();
    } catch (_) {
      if (mounted) {
        coffeeMessage(context, 'Could not sign out. Please try again.');
      }
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) => ListenableBuilder(
    listenable: widget.repository,
    builder: (context, _) {
      final repository = widget.repository;
      final user = repository.sessionUser;
      if (user == null) return const SizedBox.shrink();
      return Scaffold(
        appBar: AppBar(title: const Text('Profile')),
        body: SafeArea(
          child: Center(
            child: ConstrainedBox(
              constraints: const BoxConstraints(maxWidth: 460),
              child: ListView(
                padding: const EdgeInsets.all(24),
                children: [
                  CircleAvatar(
                    radius: 40,
                    backgroundColor: coffeeCard,
                    child: Text(
                      user.name.substring(0, 1),
                      style: const TextStyle(fontSize: 32, color: coffeeOrange),
                    ),
                  ),
                  const SizedBox(height: 16),
                  Text(
                    user.name,
                    textAlign: TextAlign.center,
                    style: const TextStyle(
                      fontSize: 28,
                      fontWeight: FontWeight.w600,
                    ),
                  ),
                  const SizedBox(height: 8),
                  Text(
                    user.isAdmin ? 'Administrator' : 'Coffee lover',
                    textAlign: TextAlign.center,
                    style: const TextStyle(color: coffeeOrange),
                  ),
                  const SizedBox(height: 32),
                  Card(
                    margin: EdgeInsets.zero,
                    child: Padding(
                      padding: const EdgeInsets.all(20),
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          const Text(
                            'Account',
                            style: TextStyle(
                              fontSize: 18,
                              fontWeight: FontWeight.w600,
                            ),
                          ),
                          const SizedBox(height: 16),
                          Text(
                            'Username: ${user.isAdmin ? 'admin' : user.name.toLowerCase()}',
                          ),
                          const SizedBox(height: 12),
                          Text('Role: ${user.role}'),
                          const SizedBox(height: 16),
                        ],
                      ),
                    ),
                  ),
                  const SizedBox(height: 16),
                  Card(
                    margin: EdgeInsets.zero,
                    child: ListTile(
                      leading: const Icon(
                        Icons.favorite_border,
                        color: coffeeOrange,
                      ),
                      title: const Text('My favorites'),
                      subtitle: Text(
                        '${repository.favorites.length} saved coffees',
                      ),
                      trailing: const Icon(Icons.chevron_right),
                      onTap: _busy
                          ? null
                          : () => Navigator.push(
                              context,
                              MaterialPageRoute<void>(
                                builder: (_) =>
                                    FavoritesScreen(repository: repository),
                              ),
                            ),
                    ),
                  ),
                  const SizedBox(height: 32),
                  OutlinedButton.icon(
                    key: const Key('sign-out'),
                    style: OutlinedButton.styleFrom(
                      minimumSize: const Size(48, 52),
                    ),
                    onPressed: _busy ? null : _signOut,
                    icon: _busy
                        ? const SizedBox.square(
                            dimension: 20,
                            child: CircularProgressIndicator(strokeWidth: 2),
                          )
                        : const Icon(Icons.logout),
                    label: Text(_busy ? 'Signing out…' : 'Sign out'),
                  ),
                ],
              ),
            ),
          ),
        ),
      );
    },
  );
}
