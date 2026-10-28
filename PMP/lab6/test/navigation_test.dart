import 'package:coffee_lab6/coffee_app.dart';
import 'package:flutter/material.dart';
import 'package:flutter_test/flutter_test.dart';

Future<void> press(WidgetTester tester, Finder finder) async {
  if (finder.evaluate().isEmpty) {
    await tester.scrollUntilVisible(
      finder,
      180,
      scrollable: find.byType(Scrollable).first,
    );
  }
  await tester.ensureVisible(finder);
  await tester.pumpAndSettle();
  await tester.tap(finder);
  await tester.pumpAndSettle();
}

void main() {
  for (final size in [const Size(320, 640), const Size(393, 873)]) {
    testWidgets(
      'three products, PageView, named arguments and cancellation $size',
      (tester) async {
        tester.view.reset();
        await tester.binding.setSurfaceSize(size);
        addTearDown(() => tester.binding.setSurfaceSize(null));
        await tester.pumpWidget(const CoffeeApp());
        await tester.pumpAndSettle();
        for (var index = 0; index < 3; index++) {
          final coffee = [coffees[0], coffees[1], coffees[5]][index];
          if (index == 0) {
            await press(tester, find.byKey(Key('product-${coffee.name}')));
          } else {
            await press(tester, find.byKey(const Key('offer-next')));
            expect(find.text('Offer ${index + 1} / 3'), findsOneWidget);
            await press(tester, find.byKey(Key('offer-order-${coffee.name}')));
          }
          final screen = tester.widget<CoffeeDetailsScreen>(
            find.byType(CoffeeDetailsScreen),
          );
          expect(screen.coffee, same(coffee));
          expect(find.text(coffee.description), findsOneWidget);
          expect(
            find.text('Catalog price: USD ${coffee.price}'),
            findsOneWidget,
          );
          expect(
            find.byWidgetPredicate(
              (w) =>
                  w is Image &&
                  w.image is AssetImage &&
                  (w.image as AssetImage).assetName == coffee.asset,
            ),
            findsWidgets,
          );
          expect(tester.takeException(), isNull);
          await press(tester, find.byTooltip('Back'));
          tester
              .state<ScrollableState>(find.byType(Scrollable).first)
              .position
              .jumpTo(0);
          await tester.pumpAndSettle();
          expect(find.byKey(const Key('cart-result')), findsNothing);
        }
        await press(tester, find.byKey(const Key('offer-previous')));
        expect(find.text('Offer 2 / 3'), findsOneWidget);
        await tester.ensureVisible(find.byKey(const Key('coffee-page-view')));
        await tester.pumpAndSettle();
        await tester.drag(
          find.byKey(const Key('coffee-page-view')),
          const Offset(260, 0),
        );
        await tester.pumpAndSettle();
        expect(find.text('Offer 1 / 3'), findsOneWidget);
        expect(tester.takeException(), isNull);
      },
    );

    testWidgets(
      'pop returns actual order, preview retains selection, popUntil clears stack $size',
      (tester) async {
        await tester.binding.setSurfaceSize(size);
        addTearDown(() => tester.binding.setSurfaceSize(null));
        await tester.pumpWidget(const CoffeeApp());
        await tester.pumpAndSettle();
        await press(tester, find.byKey(const Key('product-Espresso')));
        await press(tester, find.byTooltip('Increase quantity'));
        await press(tester, find.byKey(const Key('size-Small')));
        await press(tester, find.text('Extra Milk'));
        await press(tester, find.byKey(const Key('preview-order')));
        final order = tester
            .widget<OrderPreviewScreen>(find.byType(OrderPreviewScreen))
            .order;
        expect(order.quantity, 2);
        expect(order.size, 'Small');
        expect(order.total, 3.0);
        expect(order.options, isNot(contains('Extra Milk')));
        expect(find.text(order.summary), findsOneWidget);
        await press(tester, find.byKey(const Key('preview-back')));
        expect(find.byType(CoffeeDetailsScreen), findsOneWidget);
        await press(tester, find.byKey(const Key('add-to-cart')));
        expect(find.byType(CoffeeDetailsScreen), findsNothing);
        tester
            .state<ScrollableState>(find.byType(Scrollable).first)
            .position
            .jumpTo(0);
        await tester.pumpAndSettle();
        expect(find.text(order.summary), findsOneWidget);
        await press(tester, find.byKey(const Key('product-Espresso')));
        await press(tester, find.byKey(const Key('preview-order')));
        await press(tester, find.byKey(const Key('return-catalog')));
        expect(find.byType(OrderPreviewScreen), findsNothing);
        expect(find.byType(CoffeeDetailsScreen), findsNothing);
        tester
            .state<ScrollableState>(find.byType(Scrollable).first)
            .position
            .jumpTo(0);
        await tester.pumpAndSettle();
        expect(find.text(order.summary), findsOneWidget);
        expect(tester.takeException(), isNull);
      },
    );
  }
}
